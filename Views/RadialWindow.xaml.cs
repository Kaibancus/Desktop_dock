using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DesktopPanel.Models;
using DesktopPanel.Services;

namespace DesktopPanel.Views;

/// <summary>
/// Transparent, top-most radial launcher overlay. Shows app icons on concentric
/// rings with a center settings button. Supports hover animation, click-to-launch,
/// drag-to-reorder and drag-out-to-delete.
/// </summary>
public partial class RadialWindow : Window
{
    private const double DragThreshold = 6.0;
    private const double InnerRadius = 160.0;
    private const double RingStep = 95.0;

    private readonly AppConfig _config;
    private readonly Action _persist;
    private readonly Dictionary<string, BitmapSource?> _iconCache = new();

    private Point _center;
    private double _outerRadius;
    private readonly List<Point> _slotPositions = new();

    // Drag state
    private RadialIcon? _pressedIcon;
    private Point _pressPoint;
    private bool _dragging;

    /// <summary>
    /// When true the panel stays open (opened from the tray) so the user can
    /// drag desktop shortcuts onto it. Key-release will not hide it.
    /// </summary>
    public bool IsPinned { get; private set; }

    public event Action? RequestOpenSettings;

    public RadialWindow(AppConfig config, Action persist)
    {
        _config = config;
        _persist = persist;
        InitializeComponent();

        SizeToPrimaryScreen();
        Loaded += (_, _) => Rebuild();
        SizeChanged += (_, _) => Rebuild();
    }

    private void SizeToPrimaryScreen()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        UpdateCenter();
    }

    /// <summary>
    /// Uses the actual rendered size of the canvas so the ring stays centered
    /// even when DPI scaling makes the window's real size differ from Width/Height.
    /// </summary>
    private void UpdateCenter()
    {
        double w = PanelCanvas.ActualWidth > 0 ? PanelCanvas.ActualWidth
                 : RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth
                 : Width;
        double h = PanelCanvas.ActualHeight > 0 ? PanelCanvas.ActualHeight
                 : RootGrid.ActualHeight > 0 ? RootGrid.ActualHeight
                 : Height;
        _center = new Point(w / 2.0, h / 2.0);
    }

    public void ShowPanel()
    {
        IsPinned = false;
        SizeToPrimaryScreen();
        Rebuild();
        Opacity = 0;
        Show();
        Activate();
        Topmost = true;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Shows the panel in pinned mode (stays open until explicitly closed),
    /// so the user can drag desktop shortcuts onto the ring.
    /// </summary>
    public void ShowPinned()
    {
        SizeToPrimaryScreen();
        Rebuild();
        Opacity = 0;
        Show();
        Activate();
        Topmost = true;
        IsPinned = true;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
        BeginAnimation(OpacityProperty, fade);
    }

    public void HidePanel()
    {
        IsPinned = false;
        CancelDrag();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    /// <summary>Hides the panel only if it is not pinned (used on key-release).</summary>
    public void HideIfNotPinned()
    {
        if (!IsPinned)
            HidePanel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private Brush LabelBrush => new SolidColorBrush(ParseColor(_config.Settings.FontColor, Colors.White));
    private Color AccentColor => ParseColor(_config.Settings.AccentColor, Color.FromRgb(0x3D, 0x7E, 0xFF));

    private void Rebuild()
    {
        UpdateCenter();
        PanelCanvas.Children.Clear();
        ComputeLayout(_config.Apps.Count);

        DrawBackingDisc();

        for (int i = 0; i < _config.Apps.Count; i++)
        {
            var entry = _config.Apps[i];
            var icon = CreateIcon(entry);
            PlaceCentered(icon, _slotPositions[i]);
            PanelCanvas.Children.Add(icon);
        }

        DrawCenterButton();
    }

    private RadialIcon CreateIcon(AppEntry entry)
    {
        if (!_iconCache.TryGetValue(entry.EffectiveIconSource, out var bmp))
        {
            bmp = IconExtractor.GetIcon(entry.EffectiveIconSource);
            _iconCache[entry.EffectiveIconSource] = bmp;
        }

        var icon = new RadialIcon(entry, bmp, _config.Settings.IconSize, AccentColor, LabelBrush);
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        return icon;
    }

    private void DrawBackingDisc()
    {
        double r = _outerRadius + _config.Settings.IconSize;
        double d = r * 2;
        Color baseColor = ParseColor(_config.Settings.PanelColor, Color.FromRgb(0x1E, 0x1E, 0x1E));
        Color accent = AccentColor;
        double op = _config.Settings.PanelOpacity;

        // --- Liquid-glass disc: translucent tinted base + glossy top highlight
        //     + soft inner glow + bright rim. Layered ellipses stacked at center.

        // 0) Hit-test layer. The window background is null so empty regions let
        //    mouse clicks fall through to the desktop (so you can click desktop
        //    icons or grab a shortcut to drag in). This transparent-but-hittable
        //    disc makes only the disc area interactive / a valid drop target.
        var hit = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = Brushes.Transparent,
        };
        StackCentered(hit, r);

        // 1) Frosted translucent body — radial gradient from a lighter center to
        //    the base tint at the edge, kept semi-transparent so the desktop
        //    shows through like real glass.
        var body = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.42),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.6,
                RadiusY = 0.6,
                GradientStops =
                {
                    new GradientStop(WithAlpha(Lighten(baseColor, 0.22), op * 0.82), 0.0),
                    new GradientStop(WithAlpha(baseColor, op * 0.78), 0.72),
                    new GradientStop(WithAlpha(Darken(baseColor, 0.18), op * 0.92), 1.0),
                },
            },
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 0.6 },
            IsHitTestVisible = false,
        };
        StackCentered(body, r);

        // 2) Accent inner glow ring near the edge for a colored liquid sheen.
        var glow = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.78),
                    new GradientStop(Color.FromArgb(70, accent.R, accent.G, accent.B), 0.96),
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0),
                },
            },
            IsHitTestVisible = false,
        };
        StackCentered(glow, r);

        // 3) Glossy top highlight — an off-center white sheen, like light on glass.
        var gloss = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.12),
                Center = new Point(0.5, 0.0),
                RadiusX = 0.75,
                RadiusY = 0.55,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(28, 255, 255, 255), 0.30),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55),
                },
            },
            IsHitTestVisible = false,
        };
        StackCentered(gloss, r);

        // 4) Bright rim stroke for the crisp glass edge.
        var rim = new Ellipse
        {
            Width = d,
            Height = d,
            Stroke = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(40, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(110, accent.R, accent.G, accent.B), 1.0),
                },
            },
            StrokeThickness = 1.4,
            IsHitTestVisible = false,
        };
        StackCentered(rim, r);
    }

    private void StackCentered(FrameworkElement el, double r)
    {
        Canvas.SetLeft(el, _center.X - r);
        Canvas.SetTop(el, _center.Y - r);
        PanelCanvas.Children.Add(el);
    }

    private static Color WithAlpha(Color c, double opacity)
    {
        byte a = (byte)Math.Clamp(opacity * 255.0, 0, 255);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    private static Color Lighten(Color c, double amount)
    {
        return Color.FromRgb(
            (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
            (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
            (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));
    }

    private static Color Darken(Color c, double amount)
    {
        return Color.FromRgb(
            (byte)Math.Clamp(c.R * (1 - amount), 0, 255),
            (byte)Math.Clamp(c.G * (1 - amount), 0, 255),
            (byte)Math.Clamp(c.B * (1 - amount), 0, 255));
    }

    private void DrawCenterButton()
    {
        double size = _config.Settings.IconSize * 1.1;
        var btn = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new SolidColorBrush(AccentColor),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "\uE713", // Settings gear (Segoe MDL2 Assets)
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = size * 0.45,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            ToolTip = "Settings",
        };
        btn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RequestOpenSettings?.Invoke();
        };
        Canvas.SetLeft(btn, _center.X - size / 2);
        Canvas.SetTop(btn, _center.Y - size / 2);
        PanelCanvas.Children.Add(btn);
    }

    private void PlaceCentered(FrameworkElement el, Point center)
    {
        double s = _config.Settings.IconSize;
        Canvas.SetLeft(el, center.X - s / 2);
        Canvas.SetTop(el, center.Y - s / 2);
    }

    /// <summary>Computes ring slot positions for <paramref name="count"/> icons.</summary>
    private void ComputeLayout(int count)
    {
        _slotPositions.Clear();
        _outerRadius = InnerRadius;

        if (count == 0)
            return;

        int max = Math.Max(1, _config.Settings.MaxIconsPerRing);
        int placed = 0;
        int ring = 0;

        while (placed < count)
        {
            int onThisRing = Math.Min(max, count - placed);
            double radius = InnerRadius + ring * RingStep;
            _outerRadius = radius;

            for (int k = 0; k < onThisRing; k++)
            {
                double angle = -Math.PI / 2 + 2 * Math.PI * k / onThisRing;
                double x = _center.X + radius * Math.Cos(angle);
                double y = _center.Y + radius * Math.Sin(angle);
                _slotPositions.Add(new Point(x, y));
            }

            placed += onThisRing;
            ring++;
        }
    }

    // ---- External drop (add desktop shortcuts) ---------------------------

    private void OnDragOverPanel(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropPanel(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        bool added = false;
        foreach (var f in files)
        {
            var entry = ShortcutResolver.CreateEntry(f);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            {
                _config.Apps.Add(entry);
                added = true;
            }
        }

        if (added)
        {
            _persist();
            Rebuild();
        }
        e.Handled = true;
    }

    // ---- Drag & click handling -------------------------------------------

    private void Icon_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedIcon = (RadialIcon)sender;
        _pressPoint = e.GetPosition(PanelCanvas);
        _dragging = false;
        PanelCanvas.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressedIcon == null)
            return;

        Point p = e.GetPosition(PanelCanvas);
        if (!_dragging)
        {
            if ((p - _pressPoint).Length < DragThreshold)
                return;
            _dragging = true;
            Panel.SetZIndex(_pressedIcon, 1000);
        }

        PlaceCentered(_pressedIcon, p);

        double dist = (p - _center).Length;
        _pressedIcon.Opacity = dist > DeleteRadius ? 0.4 : 1.0;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressedIcon == null)
        {
            // Clicking empty space no longer closes the pinned panel — it stays
            // open for drag-to-add; use Esc to dismiss it.
            return;
        }

        var icon = _pressedIcon;
        bool wasDragging = _dragging;
        Point p = e.GetPosition(PanelCanvas);

        PanelCanvas.ReleaseMouseCapture();
        _pressedIcon = null;
        _dragging = false;

        if (!wasDragging)
        {
            Launch(icon.Entry);
            return;
        }

        double dist = (p - _center).Length;
        if (dist > DeleteRadius)
        {
            DeleteEntry(icon.Entry);
        }
        else
        {
            ReorderEntry(icon.Entry, p);
        }
    }

    private double DeleteRadius => _outerRadius + _config.Settings.IconSize * 1.25;

    private void ReorderEntry(AppEntry entry, Point dropPoint)
    {
        if (_slotPositions.Count == 0)
        {
            Rebuild();
            return;
        }

        int target = 0;
        double best = double.MaxValue;
        for (int i = 0; i < _slotPositions.Count; i++)
        {
            double d = (dropPoint - _slotPositions[i]).LengthSquared;
            if (d < best)
            {
                best = d;
                target = i;
            }
        }

        int oldIndex = _config.Apps.IndexOf(entry);
        if (oldIndex < 0)
        {
            Rebuild();
            return;
        }

        _config.Apps.RemoveAt(oldIndex);
        target = Math.Clamp(target, 0, _config.Apps.Count);
        _config.Apps.Insert(target, entry);

        _persist();
        Rebuild();
    }

    private void DeleteEntry(AppEntry entry)
    {
        _config.Apps.Remove(entry);
        _persist();
        Rebuild();
    }

    private void CancelDrag()
    {
        if (_pressedIcon != null)
        {
            PanelCanvas.ReleaseMouseCapture();
            _pressedIcon = null;
            _dragging = false;
        }
    }

    private void Launch(AppEntry entry)
    {
        HidePanel();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.Path,
                Arguments = entry.Arguments,
                UseShellExecute = true,
            };
            if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                psi.WorkingDirectory = entry.WorkingDirectory;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法启动 {entry.Name}:\n{ex.Message}", "DesktopPanel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
        }
        return fallback;
    }
}
