using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Services;

namespace Polaris.Views;

/// <summary>Side of the target a <see cref="WindowPreviewPopup"/> opens toward.</summary>
internal enum PreviewPlacement { Above, Below, Right, Left }

/// <summary>
/// Reusable hover-thumbnail popup. Attach it to any <see cref="FrameworkElement"/>
/// (a ring icon or a taskbar tile); when the pointer dwells over the target it
/// shows a floating panel of live window thumbnails for the associated program,
/// with click-to-activate. The element provides the windows via a delegate so
/// the same popup serves both pinned apps and taskbar-only apps.
/// </summary>
internal sealed class WindowPreviewPopup
{
    internal const int PreviewThumbWidth = 220;   // px capture width per window
    private const double PreviewOpenDelayMs = 420;
    private const double PreviewCloseDelayMs = 220;

    private readonly FrameworkElement _target;
    private readonly Func<List<WindowPreview>> _getWindows;
    private readonly int _minWindows;
    private readonly Action? _onActivated;

    private readonly DispatcherTimer _openTimer;
    private readonly DispatcherTimer _closeTimer;
    private Popup? _previewPopup;
    private bool _pointerInside;
    private bool _pointerInPopup;
    private int _previewToken;

    /// <summary>Which side of the target the popup opens toward. Defaults to
    /// Above (the main radial dock). A side dock sets this so the preview opens
    /// toward the screen interior: Below for a Top dock, Right for a Left dock,
    /// Left for a Right dock.</summary>
    public PreviewPlacement Placement { get; set; } = PreviewPlacement.Above;

    // Maps a window handle to its tile's thumbnail host so a background capture
    // can swap in the fresh image once it finishes.
    private readonly Dictionary<IntPtr, Border> _tileHosts = new();
    // Maps a window handle to its live DWM thumbnail (the preferred preview: works
    // for GPU-composited and minimized windows). Null entry = DWM registration
    // failed and the tile uses the PrintWindow still fallback instead.
    private readonly Dictionary<IntPtr, Polaris.Services.Gpu.DwmThumbnail?> _dwmThumbs = new();
    // HWND of the open popup's content (the DWM thumbnail destination); the tile
    // rects are mapped relative to this window's client area.
    private IntPtr _popupHwnd;

    // ---- Floating close ("×") button --------------------------------------
    // A taskbar-style close button. A normal WPF button inside a tile is hidden
    // behind the DWM thumbnail (an opaque overlay composited ABOVE the tile rect),
    // so the button lives in its OWN topmost popup, which the OS composites above
    // the main preview popup — hence above its DWM overlay. Shared across tiles:
    // re-anchored to whichever tile the pointer hovers, fading in when the pointer
    // enters a tile's top-right hot-zone and closing that window on click.
    private Popup? _closeBtnPopup;
    private Border? _closeBtnVisual;
    private IntPtr _closeBtnHandle;
    private bool _overCloseBtn;
    private readonly DispatcherTimer _closeBtnHideTimer;
    // Maps a window handle to its whole tile element so the shared close button
    // (which only knows the handle) can remove the correct tile from the strip.
    private readonly Dictionary<IntPtr, UIElement> _tiles = new();

    /// <param name="target">Element to anchor the popup to and centre it over.</param>
    /// <param name="getWindows">Returns the previewable windows (runs off the UI thread).</param>
    /// <param name="minWindows">Only show the popup when at least this many windows exist.</param>
    /// <param name="onActivated">Invoked after the user clicks a thumbnail.</param>
    public WindowPreviewPopup(FrameworkElement target, Func<List<WindowPreview>> getWindows,
        int minWindows, Action? onActivated)
    {
        _target = target;
        _getWindows = getWindows;
        _minWindows = minWindows;
        _onActivated = onActivated;

        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewOpenDelayMs) };
        _openTimer.Tick += OnOpenTimerTick;
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewCloseDelayMs) };
        _closeTimer.Tick += OnCloseTimerTick;

        _closeBtnHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _closeBtnHideTimer.Tick += (_, _) => { _closeBtnHideTimer.Stop(); if (!_overCloseBtn) HideCloseButton(); };
        BuildCloseButton();
    }

    /// <summary>Call from the target's MouseEnter.</summary>
    public void OnPointerEnter()
    {
        _pointerInside = true;
        _closeTimer.Stop();
        // Switching to a DIFFERENT icon: the pointer is now on another icon (it
        // cannot also be over the old popup), so close the old preview RIGHT NOW
        // instead of letting its window — and its live DWM overlay — linger through
        // the close-delay + fade while the new one's open-delay runs. A live DWM
        // preview is vivid, so any residual frame is very noticeable.
        if (_previewPopup != null)
            Close();
        _openTimer.Stop();
        _openTimer.Start();
    }

    /// <summary>Call from the target's MouseLeave.</summary>
    public void OnPointerLeave()
    {
        _pointerInside = false;
        _openTimer.Stop();
        // The DWM thumbnails are opaque overlays that don't fade with the WPF popup,
        // so on hover-away / icon switch they'd stay lit for the close-delay (and on
        // a switch, for the next open-delay) — looking like the old preview lingers.
        // Hide them at once. If the pointer is actually travelling onto the popup,
        // _pointerInPopup is already set (the popup's MouseEnter fires first), so we
        // keep them shown in that case.
        if (!_pointerInPopup)
            HideDwmThumbnails();
        // Defer closing so the pointer can travel from the target onto the popup.
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    private void OnOpenTimerTick(object? sender, EventArgs e)
    {
        _openTimer.Stop();
        if (!_pointerInside)
            return;

        int token = ++_previewToken;
        Task.Run(() =>
        {
            var windows = _getWindows();
            if (windows.Count < _minWindows)
            {
                // The pointer moved onto a target with nothing to preview (e.g. a pinned
                // app that isn't running). A prior OnPointerEnter for this target stopped
                // the close timer, so without this the previous icon's popup would stay
                // stuck open. Close it (unless the pointer is now over the popup itself).
                _target.Dispatcher.BeginInvoke(() =>
                {
                    if (token == _previewToken && !_pointerInPopup)
                        Close();
                });
                return;
            }

            // Seed each tile with any thumbnail we already cached from a previous
            // hover so the popup can pop up INSTANTLY (after the open delay)
            // instead of waiting for every slow PrintWindow capture to finish.
            foreach (var w in windows)
                w.Thumbnail = WindowPreviewService.TryGetCachedThumbnail(w.Handle);

            _target.Dispatcher.BeginInvoke(() =>
            {
                if (token != _previewToken || !_pointerInside)
                    return;
                ShowPreview(windows);
            });

            // Capture fresh frames in the background and swap each tile's image
            // in as it completes, so a stale/empty tile updates without blocking
            // the popup's appearance.
            foreach (var w in windows)
            {
                var fresh = WindowPreviewService.CaptureThumbnail(w.Handle, PreviewThumbWidth);
                if (fresh == null)
                    continue;
                IntPtr handle = w.Handle;
                _target.Dispatcher.BeginInvoke(() =>
                {
                    if (token != _previewToken)
                        return;
                    UpdateTileThumbnail(handle, fresh);
                });
            }
        });
    }

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        if (!_pointerInside && !_pointerInPopup)
            Close();
    }

    private void ShowPreview(List<WindowPreview> windows)
    {
        // Close any existing popup UI WITHOUT bumping _previewToken — the
        // background capture loop that opened this preview is still running and
        // matches the current token; bumping here would reject its tile updates.
        ClosePopupUi();
        _tileHosts.Clear();

        // Lay tiles out in a wrapping grid so ALL windows are visible (a single
        // horizontal row would overflow the popup width and, with the scrollbar
        // hidden, silently clip the extra tiles). Cap the row width at up to 6
        // columns; further windows wrap onto additional rows.
        const double TileWidth = PreviewThumbWidth + 24; // 220 content + padding + margin
        int columns = Math.Min(windows.Count, 6);
        var strip = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            MaxWidth = Math.Max(1, columns) * TileWidth,
        };
        foreach (var w in windows)
            strip.Children.Add(BuildTile(w));

        var shell = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.55,
                Color = Colors.Black,
            },
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 720,
                Content = strip,
            },
        };

        _previewPopup = new Popup
        {
            PlacementTarget = _target,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                // Open the popup toward the screen interior relative to the dock
                // edge: centred above/below for a horizontal dock, or centred to
                // the right/left for a vertical (Left/Right) side dock. A small
                // uniform gap on every side keeps the preview hugging the dock /
                // screen edge it springs from; verified the popup window is z-top
                // above the drop-shim, so a tight gap is not occluded.
                const double gap = 3;
                double x, y;
                PopupPrimaryAxis axis;
                switch (Placement)
                {
                    case PreviewPlacement.Below:
                        x = (targetSize.Width - popupSize.Width) / 2.0;
                        y = targetSize.Height + gap;
                        axis = PopupPrimaryAxis.Horizontal;
                        break;
                    case PreviewPlacement.Right:
                        x = targetSize.Width + gap;
                        y = (targetSize.Height - popupSize.Height) / 2.0;
                        axis = PopupPrimaryAxis.Vertical;
                        break;
                    case PreviewPlacement.Left:
                        x = -popupSize.Width - gap;
                        y = (targetSize.Height - popupSize.Height) / 2.0;
                        axis = PopupPrimaryAxis.Vertical;
                        break;
                    default: // Above
                        x = (targetSize.Width - popupSize.Width) / 2.0;
                        y = -popupSize.Height - gap;
                        axis = PopupPrimaryAxis.Horizontal;
                        break;
                }
                return new[] { new CustomPopupPlacement(new Point(x, y), axis) };
            },
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
            Child = shell,
        };
        shell.MouseEnter += (_, _) => { _pointerInPopup = true; _closeTimer.Stop(); };
        shell.MouseLeave += (_, _) => { _pointerInPopup = false; _closeTimer.Stop(); _closeTimer.Start(); };

        _previewPopup.IsOpen = true;

        // Once the popup is realised it has its own HWND; register a live DWM
        // thumbnail per tile into it (the preferred preview — works for GPU and
        // minimized windows). Tiles whose registration fails keep the PrintWindow
        // still / icon fallback already placed in their host. Re-place the
        // thumbnails on every layout pass so they track the tiles as the popup
        // sizes/moves; positions are cheap idempotent DwmUpdateThumbnailProperties.
        shell.LayoutUpdated += OnPopupLayoutUpdated;
        shell.Dispatcher.BeginInvoke(new Action(() => SetupDwmThumbnails(shell)),
            DispatcherPriority.Loaded);
    }

    /// <summary>After the popup is realised, register a live DWM thumbnail for each
    /// tile into the popup's HWND. Called once on open; tiles that fail keep their
    /// PrintWindow/icon fallback.</summary>
    private void SetupDwmThumbnails(Visual shell)
    {
        if (PresentationSource.FromVisual(shell) is not HwndSource src)
            return;
        _popupHwnd = src.Handle;
        _hwndRoot = src.RootVisual as UIElement;
        foreach (var kv in _tileHosts)
        {
            IntPtr handle = kv.Key;
            if (_dwmThumbs.ContainsKey(handle))
                continue;
            var thumb = Polaris.Services.Gpu.DwmThumbnail.Create(_popupHwnd, handle);
            _dwmThumbs[handle] = thumb;   // may be null (registration failed → fallback shows)
            if (thumb is { IsValid: true })
            {
                // A live DWM preview covers the host. Clear the placeholder child
                // (PrintWindow still / "已最小化" / icon) and the dark background:
                // when the overlay is hidden on hover-away the host must reveal
                // NOTHING (so the old preview vanishes cleanly) rather than flashing
                // the "已最小化" fallback that sat underneath the overlay.
                kv.Value.Child = null;
                kv.Value.Background = Brushes.Transparent;
            }
        }
        PlaceDwmThumbnails();
    }

    private void OnPopupLayoutUpdated(object? sender, EventArgs e) => PlaceDwmThumbnails();

    /// <summary>Maps each tile's thumbnail host to its on-screen rect (physical
    /// pixels, relative to the popup window's client area) and pushes it to the DWM.
    /// No-op for tiles without a valid thumbnail (they show the WPF fallback).</summary>
    private void PlaceDwmThumbnails()
    {
        if (_popupHwnd == IntPtr.Zero || _dwmThumbs.Count == 0)
            return;
        foreach (var kv in _dwmThumbs)
        {
            if (kv.Value is not { IsValid: true } thumb)
                continue;
            if (!_tileHosts.TryGetValue(kv.Key, out var host) || !host.IsVisible)
            {
                thumb.Hide();
                continue;
            }
            try
            {
                // Host rect in its own DIP space → popup-window DIP → physical px.
                var topLeft = host.TranslatePoint(new Point(0, 0), _hwndRoot ?? host);
                var dpi = VisualTreeHelper.GetDpi(host);
                int l = (int)Math.Round(topLeft.X * dpi.DpiScaleX);
                int t = (int)Math.Round(topLeft.Y * dpi.DpiScaleY);
                int r = (int)Math.Round((topLeft.X + host.ActualWidth) * dpi.DpiScaleX);
                int b = (int)Math.Round((topLeft.Y + host.ActualHeight) * dpi.DpiScaleY);
                if (r > l && b > t)
                    thumb.SetDestination(l, t, r, b);
            }
            catch { thumb.Hide(); }
        }
    }

    /// <summary>Root visual of the popup HWND, used to translate tile coordinates
    /// into the destination window's client space for the DWM thumbnail rect.</summary>
    private UIElement? _hwndRoot;

    /// <summary>Releases every DWM thumbnail registration.</summary>
    private void DisposeDwmThumbnails()
    {
        foreach (var t in _dwmThumbs.Values)
            t?.Dispose();
        _dwmThumbs.Clear();
        _popupHwnd = IntPtr.Zero;
        _hwndRoot = null;
    }

    /// <summary>Immediately hides the live DWM overlays without unregistering, so a
    /// hover-away / icon switch makes the old preview vanish at once instead of
    /// lingering for the open-delay (the overlays don't participate in the WPF
    /// fade). They are fully released later in ClosePopupUi / the next ShowPreview.</summary>
    private void HideDwmThumbnails()
    {
        foreach (var t in _dwmThumbs.Values)
            t?.Hide();
    }

    private UIElement BuildTile(WindowPreview w)
    {
        var inner = new StackPanel { Orientation = Orientation.Vertical, Width = PreviewThumbWidth };

        var thumbHost = new Border
        {
            Width = PreviewThumbWidth,
            Height = PreviewThumbWidth * 0.62,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)),
            ClipToBounds = true,
        };
        _tileHosts[w.Handle] = thumbHost;
        if (w.Thumbnail != null)
        {
            thumbHost.Child = new Image
            {
                Source = w.Thumbnail,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        else
        {
            // No live thumbnail. A genuinely minimized window can't be captured,
            // so label it as such; otherwise (e.g. Windows Terminal and other
            // GPU/DirectX-composited windows that render nothing to PrintWindow's
            // GDI surface) fall back to the app's icon rather than a misleading
            // "minimized" label.
            if (WindowPreviewService.IsWindowMinimized(w.Handle))
            {
                thumbHost.Child = new TextBlock
                {
                    Text = "已最小化",
                    Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12 * Polaris.Services.FontScale.Current,
                };
            }
            else
            {
                var appIcon = WindowPreviewService.GetWindowAppIcon(w.Handle);
                if (appIcon != null)
                {
                    thumbHost.Child = new Image
                    {
                        Source = appIcon,
                        Width = 56,
                        Height = 56,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                else
                {
                    thumbHost.Child = new TextBlock
                    {
                        Text = "无预览",
                        Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = 12 * Polaris.Services.FontScale.Current,
                    };
                }
            }
        }

        // A close ("×") button lives in a SEPARATE topmost popup (see BuildCloseButton);
        // it is revealed by the tile's top-right hover hot-zone wired up below.
        inner.Children.Add(thumbHost);

        inner.Children.Add(new TextBlock
        {
            Text = w.Title,
            Foreground = Brushes.White,
            FontSize = 12 * Polaris.Services.FontScale.Current,
            Margin = new Thickness(2, 6, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = PreviewThumbWidth,
        });

        var tile = new Border
        {
            Margin = new Thickness(4, 2, 4, 2),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = inner,
        };
        IntPtr handle = w.Handle;
        _tiles[handle] = tile;

        // Taskbar-style close button: a normal WPF button placed in the tile is
        // hidden behind the tile's DWM thumbnail (an opaque overlay composited above
        // the tile rect), so it lives in its own topmost popup (_closeBtnPopup).
        // Reveal it when the pointer enters the tile's top-right hot-zone — the DWM
        // overlay does not eat input, so thumbHost still receives these moves.
        const double hotW = 46, hotH = 40;
        thumbHost.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(thumbHost);
            bool inCorner = p.X >= thumbHost.ActualWidth - hotW && p.Y <= hotH;
            if (inCorner)
                ShowCloseButtonFor(handle, thumbHost);
            else if (!_overCloseBtn)
                ScheduleHideCloseButton();
        };
        thumbHost.MouseLeave += (_, _) =>
        {
            if (!_overCloseBtn)
                ScheduleHideCloseButton();
        };
        tile.MouseEnter += (_, _) =>
            tile.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) =>
            tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            WindowPreviewService.Activate(handle);
            Close();
            _onActivated?.Invoke();
        };
        return tile;
    }

    /// <summary>Builds the shared floating close button and its topmost popup. The
    /// popup is re-anchored to a tile's top-right corner in
    /// <see cref="ShowCloseButtonFor"/>; living in its own HWND keeps it visible
    /// above the DWM thumbnail overlay that hides any in-tile WPF button.</summary>
    private void BuildCloseButton()
    {
        _closeBtnVisual = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xA6, 0x22, 0x1A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "✕",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _closeBtnVisual.MouseEnter += (_, _) =>
        {
            _overCloseBtn = true;
            _closeBtnHideTimer.Stop();
            // The button is a separate HWND, so moving onto it fires the shell's
            // MouseLeave; treat it as "still in the popup" so the preview stays open.
            _pointerInPopup = true;
            _closeTimer.Stop();
            _closeBtnVisual!.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xC2, 0x32, 0x26));
        };
        _closeBtnVisual.MouseLeave += (_, _) =>
        {
            _overCloseBtn = false;
            _closeBtnVisual!.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xA6, 0x22, 0x1A));
            _pointerInPopup = false;
            _closeTimer.Stop();
            _closeTimer.Start();
            ScheduleHideCloseButton();
        };
        _closeBtnVisual.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CloseHoveredWindow();
        };
        _closeBtnPopup = new Popup
        {
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
                new[] { new CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width - 2, 1), PopupPrimaryAxis.None) },
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
            Child = _closeBtnVisual,
        };
    }

    /// <summary>Anchors and fades the shared close button into the given tile's
    /// top-right corner.</summary>
    private void ShowCloseButtonFor(IntPtr handle, Border thumbHost)
    {
        if (_closeBtnPopup == null)
            return;
        _closeBtnHideTimer.Stop();
        _closeBtnHandle = handle;
        if (!ReferenceEquals(_closeBtnPopup.PlacementTarget, thumbHost))
        {
            // Re-anchoring to another tile: close first so the Fade replays at the
            // new corner instead of the button jumping across.
            _closeBtnPopup.IsOpen = false;
            _closeBtnPopup.PlacementTarget = thumbHost;
        }
        if (!_closeBtnPopup.IsOpen)
            _closeBtnPopup.IsOpen = true;
    }

    private void ScheduleHideCloseButton()
    {
        _closeBtnHideTimer.Stop();
        _closeBtnHideTimer.Start();
    }

    private void HideCloseButton()
    {
        if (_closeBtnPopup != null)
            _closeBtnPopup.IsOpen = false;
    }

    /// <summary>Closes the window the floating button is anchored to and removes its
    /// tile, closing the whole popup once the last tile is gone.</summary>
    private void CloseHoveredWindow()
    {
        IntPtr handle = _closeBtnHandle;
        _overCloseBtn = false;
        HideCloseButton();
        WindowPreviewService.CloseWindow(handle);
        _tileHosts.Remove(handle);
        if (_dwmThumbs.TryGetValue(handle, out var dt))
        {
            dt?.Dispose();
            _dwmThumbs.Remove(handle);
        }
        if (_tiles.TryGetValue(handle, out var tileEl))
        {
            _tiles.Remove(handle);
            if (tileEl is FrameworkElement fe && fe.Parent is Panel parent)
            {
                parent.Children.Remove(tileEl);
                if (parent.Children.Count == 0)
                    Close();
            }
        }
    }

    /// <summary>Swaps a freshly-captured thumbnail into its tile (called on the
    /// UI thread once a background capture finishes).</summary>
    private void UpdateTileThumbnail(IntPtr handle, BitmapSource thumb)
    {
        // A live DWM thumbnail (if it registered) is the preferred preview and sits
        // as an overlay above this host, so don't bother swapping in the slower
        // PrintWindow still — it would only show if the DWM overlay later failed.
        if (_dwmThumbs.TryGetValue(handle, out var dt) && dt is { IsValid: true })
            return;
        if (!_tileHosts.TryGetValue(handle, out var host))
            return;
        host.Child = new Image
        {
            Source = thumb,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>True while the thumbnail popup is currently shown.</summary>
    public bool IsOpen => _previewPopup != null;

    /// <summary>Closes and disposes the preview popup if it is open.</summary>
    public void Close()
    {
        _previewToken++;            // invalidate any in-flight capture
        _openTimer.Stop();
        _closeTimer.Stop();
        _pointerInPopup = false;
        ClosePopupUi();
    }

    /// <summary>
    /// Dismisses the popup while letting its fade-out animation actually play
    /// (unlike <see cref="Close"/>, which blanks the popup instantly), then
    /// invokes <paramref name="onClosed"/> once the fade has visibly finished.
    /// Used so a right-click context menu only appears after the thumbnail
    /// preview has animated away rather than overlapping it.
    /// </summary>
    public void CloseAnimated(Action onClosed)
    {
        _previewToken++;            // invalidate any in-flight capture
        _openTimer.Stop();
        _closeTimer.Stop();
        _pointerInPopup = false;

        var popup = _previewPopup;
        if (popup == null)
        {
            onClosed();
            return;
        }
        // Detach so a concurrent hover can't reuse this fading popup, but keep
        // its Child intact so PopupAnimation.Fade has something to fade out.
        _previewPopup = null;
        // DWM thumbnails are opaque overlays that do NOT participate in the WPF
        // fade-out, so release them immediately rather than leaving a live preview
        // hanging over a fading popup.
        HideCloseButton();
        _tiles.Clear();
        DisposeDwmThumbnails();
        _tileHosts.Clear();

        popup.IsOpen = false;       // begins the fade-out

        // PopupAnimation.Fade runs for ~200 ms; wait that out (plus a little
        // margin) before tearing the popup down and signalling completion.
        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(230) };
        done.Tick += (_, _) =>
        {
            done.Stop();
            popup.Child = null;
            onClosed();
        };
        done.Start();
    }

    /// <summary>Tears down the popup window only, leaving _previewToken and the
    /// timers untouched (used by ShowPreview when replacing a prior popup while
    /// the same capture batch keeps streaming in fresh thumbnails).</summary>
    private void ClosePopupUi()
    {
        HideCloseButton();
        _tiles.Clear();
        DisposeDwmThumbnails();
        _tileHosts.Clear();
        if (_previewPopup != null)
        {
            _previewPopup.IsOpen = false;
            _previewPopup.Child = null;
            _previewPopup = null;
        }
    }
}
