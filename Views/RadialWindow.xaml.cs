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
    private const double InnerRadius = 140.0;
    private const double RingStep = 88.0;
    private const int Ring0Cap = 12;
    private const int Ring1Cap = 24;

    // Outer-ring icons are drawn slightly larger than inner-ring icons.
    private const double OuterIconScale = 1.18;

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

    // Icons in current _config.Apps order, parallel to the entries. Used to
    // animate the non-dragged icons aside while reordering.
    private readonly List<RadialIcon> _iconElements = new();

    // Slot the dragged icon is currently hovering toward, expressed as a target
    // ring (0 = inner, 1 = outer, -1 = none) and angular position within it.
    private int _dragTargetRing = -1;
    private int _dragTargetPos = -1;

    // Icon the pointer is currently hovering (for the "spread apart" effect).
    private RadialIcon? _hoverIcon;

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
        _iconElements.Clear();
        _hoverIcon = null;
        ComputeLayout(_config.Apps.Count);

        DrawBackingDisc();

        int r0 = EffectiveRing0Count(_config.Apps.Count);
        for (int i = 0; i < _config.Apps.Count; i++)
        {
            var entry = _config.Apps[i];
            double size = i < r0 ? _config.Settings.IconSize
                                 : _config.Settings.IconSize * OuterIconScale;
            var icon = CreateIcon(entry, size);
            PlaceCentered(icon, _slotPositions[i]);
            PanelCanvas.Children.Add(icon);
            _iconElements.Add(icon);
        }

        DrawCenterButton();
    }

    private RadialIcon CreateIcon(AppEntry entry, double iconSize)
    {
        if (!_iconCache.TryGetValue(entry.EffectiveIconSource, out var bmp))
        {
            bmp = IconExtractor.GetIcon(entry.EffectiveIconSource);
            _iconCache[entry.EffectiveIconSource] = bmp;
        }

        var icon = new RadialIcon(entry, bmp, iconSize, AccentColor, LabelBrush);
        icon.PreviewMouseLeftButtonDown += Icon_PreviewMouseLeftButtonDown;
        icon.HoverStarted += OnIconHoverStarted;
        icon.HoverEnded += OnIconHoverEnded;
        return icon;
    }

    private void DrawBackingDisc()
    {
        double icon = _config.Settings.IconSize;
        double outerIcon = icon * OuterIconScale;
        double r = _outerRadius + outerIcon;
        double d = r * 2;

        // --- Saturn-ring background -------------------------------------------
        // The centre is the planet (drawn by DrawCenterButton); the icon rings
        // ride on top of Saturn's banded rings drawn here.

        // Hit-test layer so only the disc area is interactive / a drop target.
        var hit = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = Brushes.Transparent,
        };
        StackCentered(hit, r);

        bool hasOuter = _outerRadius > InnerRadius + 0.5;

        // Tight, equidistant spacing between adjacent bands within a ring group
        // (smaller gap + wider bands => denser, more solid-looking rings).
        double gap = icon * 0.30;

        // Inner Saturn ring: 3 bands, equally spaced; the inner-ring icons
        // (centred at InnerRadius) sit on band 2 (the middle band).
        double[] innerBands =
        {
            InnerRadius - gap,
            InnerRadius,
            InnerRadius + gap,
        };

        if (hasOuter)
        {
            // Outer Saturn ring: 5 bands, equally spaced; the outer-ring icons
            // (centred at InnerRadius + RingStep) sit on band 3 (the middle).
            double ro = InnerRadius + RingStep;
            double[] outerBands =
            {
                ro - 2 * gap,
                ro - gap,
                ro,
                ro + gap,
                ro + 2 * gap,
            };
            DrawSaturnRingBands(outerBands, icon * 0.20);
        }

        DrawSaturnRingBands(innerBands, icon * 0.24);
    }

    /// <summary>
    /// Draws a set of concentric Saturn-ring bands at the given
    /// <paramref name="radii"/>, each <paramref name="thickness"/> thick, with tan
    /// colouring that varies across the set and fades slightly at the ends.
    /// </summary>
    private void DrawSaturnRingBands(double[] radii, double thickness)
    {
        Color tanDark = Color.FromRgb(0x7A, 0x60, 0x3C);
        Color tanMid = Color.FromRgb(0xC9, 0xA8, 0x76);
        Color tanLight = Color.FromRgb(0xF2, 0xE2, 0xB6);

        int n = radii.Length;
        for (int i = 0; i < n; i++)
        {
            double rr = radii[i];
            if (rr <= 1)
                continue;

            double t = n == 1 ? 0.5 : i / (double)(n - 1);
            double s = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2.0);
            Color shade = s < 0.5
                ? LerpColor(tanDark, tanMid, s * 2.0)
                : LerpColor(tanMid, tanLight, (s - 0.5) * 2.0);
            double edgeFade = 1.0 - Math.Pow(Math.Abs(t - 0.5) * 2.0, 2.4);
            double alpha = Math.Clamp(0.55 + 0.25 * edgeFade, 0, 1);

            var ring = new Ellipse
            {
                Width = rr * 2,
                Height = rr * 2,
                Stroke = new SolidColorBrush(WithAlpha(shade, alpha)),
                StrokeThickness = thickness,
                IsHitTestVisible = false,
            };
            StackCentered(ring, rr);
        }

        // Soft highlight on the outermost band's edge.
        double outer = radii[n - 1];
        var rim = new Ellipse
        {
            Width = outer * 2,
            Height = outer * 2,
            Stroke = new SolidColorBrush(Color.FromArgb(80, 0xFF, 0xF4, 0xD8)),
            StrokeThickness = 1.0,
            IsHitTestVisible = false,
        };
        StackCentered(rim, outer);
    }

    private static Color LerpColor(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
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
        double size = _config.Settings.IconSize * 2.0;
        double r = size / 2;

        // Saturn planet at the centre. Click opens settings; hovering slowly
        // rotates the atmospheric bands. Hosted in a Grid so it can scale/rotate
        // around its centre.
        var root = new Grid
        {
            Width = size,
            Height = size,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // keep the full square hit-testable
            ToolTip = "设置",
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        // Soft drop shadow under the planet for depth.
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 22,
            ShadowDepth = 0,
            Opacity = 0.55,
        };

        Color amber = Color.FromRgb(0xD8, 0xB4, 0x76);
        Color amberDark = Color.FromRgb(0x6E, 0x52, 0x2E);
        Color amberLight = Color.FromRgb(0xF6, 0xE6, 0xBE);

        // Circular planet body with a clip so the bands stay inside the globe.
        var globe = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(r),
            // Clip to a true circle so the atmospheric bands never fill the
            // square corners (ClipToBounds alone would clip to the rectangle).
            Clip = new EllipseGeometry(new Point(r, r), r, r),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.36, 0.30),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.72,
                RadiusY = 0.72,
                GradientStops =
                {
                    new GradientStop(amberLight, 0.0),
                    new GradientStop(amber, 0.5),
                    new GradientStop(Darken(amber, 0.25), 0.82),
                    new GradientStop(amberDark, 1.0),
                },
            },
        };

        // Atmospheric texture that scrolls horizontally to simulate the planet
        // spinning on its own axis (self-rotation), rather than the whole disc
        // tumbling. The texture is drawn twice (a seamless tile of width `size`)
        // and translated left; clipped to the circular globe.
        var bandsScroll = new TranslateTransform(0, 0);
        var bands = new Canvas
        {
            Width = size * 2,
            Height = size,
            RenderTransform = bandsScroll,
        };

        // Builds one tile of latitude bands + a few storm spots at x-offset ox.
        void BuildBandTile(double ox)
        {
            int bandCount = 9;
            for (int i = 0; i < bandCount; i++)
            {
                double t = (i + 0.5) / bandCount;          // 0..1 top->bottom
                double y = t * size;
                double h = size / bandCount * (0.6 + 0.5 * Math.Sin(i * 1.7));
                h = Math.Max(3, h);
                double s = 0.5 + 0.5 * Math.Sin(i * 2.1);
                Color shade = s < 0.5
                    ? LerpColor(amberDark, amber, s * 2.0)
                    : LerpColor(amber, amberLight, (s - 0.5) * 2.0);
                byte a = (byte)(60 + 70 * Math.Abs(Math.Sin(i * 1.3)));
                var band = new System.Windows.Shapes.Rectangle
                {
                    Width = size,
                    Height = h,
                    Fill = new SolidColorBrush(Color.FromArgb(a, shade.R, shade.G, shade.B)),
                };
                Canvas.SetLeft(band, ox);
                Canvas.SetTop(band, y - h / 2);
                bands.Children.Add(band);
            }

            // Storm spots give the horizontal motion something to "carry",
            // so the self-rotation reads clearly. Positions are tile-relative.
            (double fx, double fy, double fw, double fh, Color c, byte a)[] spots =
            {
                (0.30, 0.40, 0.26, 0.12, Lighten(amber, 0.20), 120),
                (0.62, 0.58, 0.18, 0.10, Darken(amber, 0.22), 110),
                (0.82, 0.34, 0.14, 0.08, amberLight, 90),
                (0.14, 0.66, 0.16, 0.09, Darken(amber, 0.18), 100),
            };
            foreach (var sp in spots)
            {
                var spot = new Ellipse
                {
                    Width = size * sp.fw,
                    Height = size * sp.fh,
                    Fill = new SolidColorBrush(Color.FromArgb(sp.a, sp.c.R, sp.c.G, sp.c.B)),
                };
                Canvas.SetLeft(spot, ox + size * sp.fx - size * sp.fw / 2);
                Canvas.SetTop(spot, size * sp.fy - size * sp.fh / 2);
                bands.Children.Add(spot);
            }
        }
        BuildBandTile(0);
        BuildBandTile(size);
        globe.Child = bands;
        root.Children.Add(globe);

        // Terminator shadow: darken the lower-right to give a spherical feel.
        var shadow = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.68, 0.72),
                Center = new Point(0.68, 0.72),
                RadiusX = 0.85,
                RadiusY = 0.85,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.0),
                    new GradientStop(Color.FromArgb(40, 0, 0, 0), 0.5),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.85),
                },
            },
        };
        root.Children.Add(shadow);

        // Specular highlight on the upper-left.
        var highlight = new Ellipse
        {
            Width = size * 0.9,
            Height = size * 0.9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.34, 0.26),
                Center = new Point(0.30, 0.22),
                RadiusX = 0.6,
                RadiusY = 0.6,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.0),
                    new GradientStop(Color.FromArgb(30, 255, 255, 255), 0.35),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6),
                },
            },
        };
        root.Children.Add(highlight);

        // Crisp rim around the globe.
        var rim = new Ellipse
        {
            Width = size,
            Height = size,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(120, 0xFF, 0xF0, 0xCE)),
            StrokeThickness = 1.2,
        };
        root.Children.Add(rim);

        // --- Self-rotation: scroll the band texture horizontally. Always spins
        // slowly (a planet is always turning); hovering speeds it up. The tile
        // repeats every `size`, so looping any one-tile range is seamless. -----
        void StartScroll(double secondsPerTurn)
        {
            double cur = bandsScroll.X;
            // Normalise into [-size, 0] so the value never runs away; identical
            // content thanks to the tiled texture.
            cur = -(((-cur) % size + size) % size);
            bandsScroll.BeginAnimation(TranslateTransform.XProperty, null);
            bandsScroll.X = cur;
            var anim = new DoubleAnimation(cur, cur - size,
                TimeSpan.FromSeconds(secondsPerTurn))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            bandsScroll.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        StartScroll(18.0);                       // gentle idle self-rotation
        root.MouseEnter += (_, _) => StartScroll(6.0);
        root.MouseLeave += (_, _) => StartScroll(18.0);

        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RequestOpenSettings?.Invoke();
        };
        Panel.SetZIndex(root, 2000); // keep Saturn above the ring bands
        Canvas.SetLeft(root, _center.X - size / 2);
        Canvas.SetTop(root, _center.Y - size / 2);
        PanelCanvas.Children.Add(root);
    }

    private void PlaceCentered(FrameworkElement el, Point center)
    {
        double s = el is RadialIcon ri ? ri.IconSize : _config.Settings.IconSize;
        Canvas.SetLeft(el, center.X - s / 2);
        Canvas.SetTop(el, center.Y - s / 2);
    }

    /// <summary>Computes ring slot positions for <paramref name="count"/> icons,
    /// split across up to two rings per the inner-ring count.</summary>
    private void ComputeLayout(int count)
    {
        _slotPositions.Clear();
        _outerRadius = InnerRadius;

        if (count <= 0)
            return;

        int r0 = EffectiveRing0Count(count);
        _slotPositions.AddRange(SlotPositionsFor(count, r0));
        _outerRadius = (count - r0 > 0) ? InnerRadius + RingStep : InnerRadius;
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
        _dragTargetRing = -1;
        _dragTargetPos = -1;
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
            // Stop any residual reflow animation on the dragged icon so it tracks
            // the cursor exactly.
            _pressedIcon.BeginAnimation(Canvas.LeftProperty, null);
            _pressedIcon.BeginAnimation(Canvas.TopProperty, null);
        }

        PlaceCentered(_pressedIcon, p);

        double dist = (p - _center).Length;
        _pressedIcon.Opacity = dist > DeleteRadius ? 0.4 : 1.0;

        // Push other icons aside to reveal the slot the dragged icon is over.
        // Skip while the icon is dragged out past the outer ring (delete zone).
        if (dist <= DeleteRadius)
        {
            int src = _iconElements.IndexOf(_pressedIcon);
            var (ring, pos) = ComputeDragTarget(p, src);
            if (ring != _dragTargetRing || pos != _dragTargetPos)
            {
                _dragTargetRing = ring;
                _dragTargetPos = pos;
                ReflowAround(ring, pos);
            }
        }
        else if (_dragTargetRing != -1)
        {
            // Dragged into the delete zone — snap the others back to their slots.
            _dragTargetRing = -1;
            _dragTargetPos = -1;
            RestoreSlots();
        }
    }

    /// <summary>
    /// Determines which ring (0 inner / 1 outer) and angular position the dragged
    /// icon is targeting, honouring the per-ring caps and creating the outer ring
    /// when the icon is dragged out to that distance.
    /// </summary>
    private (int ring, int pos) ComputeDragTarget(Point p, int src)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int o0 = (src >= 0 && src < r0) ? r0 - 1 : r0; // other icons on the inner ring
        int m = Math.Max(0, n - 1);
        int ring1Others = m - o0;

        double dist = (p - _center).Length;
        double ringMid = (InnerRadius + (InnerRadius + RingStep)) / 2.0;
        int ring = dist <= ringMid ? 0 : 1;

        // Respect caps: redirect to the other ring if the chosen one is full.
        if (ring == 0 && o0 + 1 > Ring0Cap)
            ring = 1;
        if (ring == 1 && ring1Others + 1 > Ring1Cap)
            ring = 0;

        int slotsAfter = ring == 0 ? o0 + 1 : ring1Others + 1;
        double ang = Math.Atan2(p.Y - _center.Y, p.X - _center.X);
        double fromTop = ang + Math.PI / 2.0; // 0 at 12 o'clock, clockwise
        fromTop = ((fromTop % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        int pos = (int)Math.Round(fromTop / (2 * Math.PI) * slotsAfter);
        pos = Math.Clamp(pos, 0, Math.Max(0, slotsAfter - 1));
        return (ring, pos);
    }

    /// <summary>
    /// Number of icons on the inner ring for <paramref name="n"/> total icons,
    /// derived from the persisted <c>Ring0Count</c> (0 = auto) and clamped to the
    /// per-ring caps (inner ≤ 12, outer ≤ 24).
    /// </summary>
    private int EffectiveRing0Count(int n)
    {
        if (n <= 0)
            return 0;

        int r0 = _config.Settings.Ring0Count;
        if (r0 <= 0 || r0 > n)
            r0 = Math.Min(Ring0Cap, n); // auto: fill the inner ring first

        r0 = Math.Clamp(r0, 1, Math.Min(Ring0Cap, n));

        // If the outer ring would overflow its cap, push more onto the inner ring.
        if (n - r0 > Ring1Cap)
            r0 = Math.Min(Ring0Cap, n);
        return r0;
    }

    /// <summary>Builds the slot centres for a layout of <paramref name="n"/> icons
    /// with <paramref name="r0"/> of them on the inner ring.</summary>
    private List<Point> SlotPositionsFor(int n, int r0)
    {
        var list = new List<Point>(Math.Max(0, n));
        if (n <= 0)
            return list;

        r0 = Math.Clamp(r0, 1, n);
        int ring1 = n - r0;
        for (int k = 0; k < r0; k++)
            list.Add(RingPoint(InnerRadius, k, r0));
        for (int k = 0; k < ring1; k++)
            list.Add(RingPoint(InnerRadius + RingStep, k, ring1));
        return list;
    }

    private Point RingPoint(double radius, int k, int count)
    {
        double angle = -Math.PI / 2 + 2 * Math.PI * k / Math.Max(1, count);
        return new Point(_center.X + radius * Math.Cos(angle),
                         _center.Y + radius * Math.Sin(angle));
    }

    /// <summary>
    /// Maps each entry index to its flat slot when the dragged entry
    /// <paramref name="src"/> is inserted into <paramref name="ring"/> at angular
    /// position <paramref name="pos"/>; also returns the resulting inner-ring count.
    /// </summary>
    private (int[] slotOfEntry, int newR0) ComputeArrangement(int src, int ring, int pos)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int srcRing = src < r0 ? 0 : 1;

        // Current angular sequences per ring. Rebuild() places entry i at flat
        // slot i, so the inner ring is entries 0..r0-1 and the outer ring is the
        // rest, both already in angular (clockwise-from-top) order.
        var inner = new List<int>();
        for (int i = 0; i < r0; i++)
            inner.Add(i);
        var outer = new List<int>();
        for (int i = r0; i < n; i++)
            outer.Add(i);

        int newR0;
        if (ring == srcRing)
        {
            // Same ring: shift only the icons on the shorter arc between the
            // dragged icon's current angular slot and the target slot, so
            // crossing the 12 o'clock boundary nudges neighbours instead of
            // rotating the whole ring.
            var seq = ring == 0 ? inner : outer;
            int len = seq.Count;
            int cur = seq.IndexOf(src);
            int tgt = Math.Clamp(pos, 0, Math.Max(0, len - 1));
            int[] newIdx = ShortestArcShift(len, cur, tgt);

            var newSeq = new int[len];
            for (int j = 0; j < len; j++)
                newSeq[newIdx[j]] = seq[j];

            seq.Clear();
            seq.AddRange(newSeq);
            newR0 = r0;
        }
        else
        {
            // Cross ring: remove from the source ring and insert into the target
            // ring at the angular position. Both rings re-space by their new
            // counts, which is the expected behaviour when moving between rings.
            if (srcRing == 0)
                inner.Remove(src);
            else
                outer.Remove(src);

            var tgt = ring == 0 ? inner : outer;
            int insertAt = Math.Clamp(pos, 0, tgt.Count);
            tgt.Insert(insertAt, src);
            newR0 = inner.Count;
        }

        int[] slotOfEntry = new int[n];
        int slot = 0;
        foreach (int e in inner)
            slotOfEntry[e] = slot++;
        foreach (int e in outer)
            slotOfEntry[e] = slot++;
        return (slotOfEntry, newR0);
    }

    /// <summary>
    /// For a ring of <paramref name="len"/> slots, returns the new angular index
    /// of each current index when the icon at <paramref name="cur"/> moves to
    /// <paramref name="tgt"/>, shifting only the shorter arc between them by one.
    /// </summary>
    private static int[] ShortestArcShift(int len, int cur, int tgt)
    {
        int[] newIdx = new int[Math.Max(0, len)];
        if (len <= 0)
            return newIdx;

        cur = Math.Clamp(cur, 0, len - 1);
        tgt = Math.Clamp(tgt, 0, len - 1);
        newIdx[cur] = tgt;

        int df = ((tgt - cur) % len + len) % len; // forward steps cur -> tgt
        int db = len - df;                          // backward steps
        bool forward = df <= db;

        for (int j = 0; j < len; j++)
        {
            if (j == cur)
                continue;
            int ns = j;
            if (forward)
            {
                int rel = ((j - cur) % len + len) % len; // 1..len-1
                if (rel >= 1 && rel <= df)
                    ns = (j - 1 + len) % len;
            }
            else
            {
                int relT = ((j - tgt) % len + len) % len; // 0..len-1
                if (relT >= 0 && relT < db)
                    ns = (j + 1) % len;
            }
            newIdx[j] = ns;
        }
        return newIdx;
    }

    // ---- Hover "spread apart" --------------------------------------------

    /// <summary>
    /// On hover, raise the icon above its neighbours and push the nearby icons
    /// radially away so the enlarged icon + its name label have room to breathe.
    /// </summary>
    private void OnIconHoverStarted(RadialIcon ic)
    {
        // Ignore hover effects while a drag is in progress.
        if (_pressedIcon != null)
            return;

        int idx = _iconElements.IndexOf(ic);
        if (idx < 0)
            return;

        _hoverIcon = ic;
        Panel.SetZIndex(ic, 500);
        SpreadNeighbours(idx);
    }

    private void OnIconHoverEnded(RadialIcon ic)
    {
        if (_pressedIcon != null)
            return;

        int idx = _iconElements.IndexOf(ic);
        if (idx >= 0)
            Panel.SetZIndex(ic, 0);

        if (_hoverIcon == ic)
            _hoverIcon = null;

        RestoreSlots();
    }

    /// <summary>
    /// Pushes icons near <paramref name="hovered"/> away from it, with the shift
    /// falling off by distance, so closer neighbours move more.
    /// </summary>
    private void SpreadNeighbours(int hovered)
    {
        double iconSize = _config.Settings.IconSize;
        double push = iconSize * 0.75;
        double influence = iconSize * 2.7;
        Point hp = _slotPositions[hovered];

        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (i == hovered)
                continue;

            Vector v = _slotPositions[i] - hp;
            double d = v.Length;
            if (d > 0.01 && d < influence)
            {
                double amount = push * (1 - d / influence);
                Point np = _slotPositions[i] + (v / d) * amount;
                AnimateTo(_iconElements[i], np);
            }
            else
            {
                AnimateTo(_iconElements[i], _slotPositions[i]);
            }
        }
    }

    /// <summary>Animates all icons back to their home ring slots.</summary>
    private void RestoreSlots()
    {
        for (int i = 0; i < _iconElements.Count && i < _slotPositions.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            AnimateTo(_iconElements[i], _slotPositions[i]);
        }
    }

    /// <summary>
    /// Animates every non-dragged icon to its slot in the prospective layout
    /// where the dragged icon occupies (<paramref name="ring"/>, <paramref name="pos"/>),
    /// producing the "make room" effect across both rings.
    /// </summary>
    private void ReflowAround(int ring, int pos)
    {
        int src = _iconElements.IndexOf(_pressedIcon!);
        if (src < 0)
            return;

        var (slotOfEntry, newR0) = ComputeArrangement(src, ring, pos);
        int n = _config.Apps.Count;
        var positions = SlotPositionsFor(n, newR0);
        for (int i = 0; i < _iconElements.Count; i++)
        {
            if (_iconElements[i] == _pressedIcon)
                continue;
            int slot = slotOfEntry[i];
            if (slot >= 0 && slot < positions.Count)
                AnimateTo(_iconElements[i], positions[slot]);
        }
    }

    /// <summary>Smoothly slides an icon to a new slot center.</summary>
    private void AnimateTo(FrameworkElement el, Point center)
    {
        double s = el is RadialIcon ri ? ri.IconSize : _config.Settings.IconSize;
        double left = center.X - s / 2;
        double top = center.Y - s / 2;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var la = new DoubleAnimation(left, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var ta = new DoubleAnimation(top, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        el.BeginAnimation(Canvas.LeftProperty, la);
        el.BeginAnimation(Canvas.TopProperty, ta);
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
        int ring = _dragTargetRing;
        int pos = _dragTargetPos;

        PanelCanvas.ReleaseMouseCapture();
        _pressedIcon = null;
        _dragging = false;
        _dragTargetRing = -1;
        _dragTargetPos = -1;

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
            CommitArrangement(icon.Entry, ring, pos, p);
        }
    }

    /// <summary>Distance past the outer ring beyond which a dropped icon is deleted.</summary>
    private double DeleteRadius => InnerRadius + RingStep + _config.Settings.IconSize * 1.25;

    private void CommitArrangement(AppEntry entry, int ring, int pos, Point dropPoint)
    {
        int src = _config.Apps.IndexOf(entry);
        if (src < 0)
        {
            Rebuild();
            return;
        }

        if (ring < 0)
            (ring, pos) = ComputeDragTarget(dropPoint, src);

        var (slotOfEntry, newR0) = ComputeArrangement(src, ring, pos);
        int n = _config.Apps.Count;

        // Reorder the entries by their new slot so Rebuild() (entry i -> slot i)
        // reproduces the arrangement, and persist the new inner-ring count.
        var ordered = new AppEntry[n];
        for (int i = 0; i < n; i++)
            ordered[slotOfEntry[i]] = _config.Apps[i];

        _config.Apps.Clear();
        foreach (var a in ordered)
            _config.Apps.Add(a);

        _config.Settings.Ring0Count = Math.Clamp(newR0, 0, n);

        _persist();
        Rebuild();
    }

    private void DeleteEntry(AppEntry entry)
    {
        int n = _config.Apps.Count;
        int r0 = EffectiveRing0Count(n);
        int idx = _config.Apps.IndexOf(entry);

        _config.Apps.Remove(entry);

        // If an inner-ring icon was removed, keep the inner-ring count in step.
        if (idx >= 0 && idx < r0)
            _config.Settings.Ring0Count = Math.Max(0, r0 - 1);

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
            _dragTargetRing = -1;
            _dragTargetPos = -1;
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
