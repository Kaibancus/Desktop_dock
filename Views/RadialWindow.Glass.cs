using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Polaris.Services;

namespace Polaris.Views;

// Non-Saturn theme rendering: the translucent "liquid glass" panel and the
// minimal centre button used by plain grid themes.
public partial class RadialWindow
{
    /// <summary>A minimal centre control used by non-Saturn themes: a small
    /// circular settings button placed above the grid.</summary>
    private void DrawSimpleCenterButton()
    {
        double s = Math.Max(40, EffectiveIconSize * 0.82);
        var btn = new Border
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(s / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x2A, 0x6C, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1.6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.55,
                Color = Color.FromRgb(0x0A, 0x10, 0x1C),
            },
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = s * 0.54,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; RequestOpenSettings?.Invoke(); };

        double gridTop = _center.Y - 2 * (EffectiveIconSize * 2.1);
        Canvas.SetLeft(btn, _center.X - s / 2);
        Canvas.SetTop(btn, gridTop - s - EffectiveIconSize * 0.6);
        Panel.SetZIndex(btn, 2000);
        PanelCanvas.Children.Add(btn);
    }

    /// <summary>
    /// Draws the "液态玻璃" backdrop: a translucent, frosted rounded-rectangle
    /// panel sized to enclose the 5-column icon grid (5×4 by default, growing to
    /// 5×5). Its overall opacity follows the user's panel-transparency setting.
    /// </summary>
    private void DrawGlassPanel()
    {
        double icon = EffectiveIconSize;
        double cellW = icon * 2.15;
        double cellH = icon * 2.35;
        int rows = LiquidGlassTheme.RowsFor(_config.Apps.Count);
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        double gridH = (rows - 1) * cellH;

        double padX = icon * 1.15;
        double padY = icon * 1.15;
        double w = gridW + icon + padX * 2;
        double h = gridH + icon + padY * 2;
        double left = _center.X - w / 2.0;
        double top = GlassDockCenter.Y - h / 2.0;

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        const double radius = 28;

        DrawGlassSlab(left, top, w, h, radius, opacity);

        // Settings gear in the panel's top-right corner.
        double gs = Math.Max(26, icon * 0.5);
        var gear = new Border
        {
            Width = gs,
            Height = gs,
            CornerRadius = new CornerRadius(gs / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = gs * 0.55,
                Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        gear.MouseLeftButtonUp += (_, e) => { e.Handled = true; RequestOpenSettings?.Invoke(); };
        Canvas.SetLeft(gear, left + w - gs - 12);
        Canvas.SetTop(gear, top + 12);
        Panel.SetZIndex(gear, 2000);
        PanelCanvas.Children.Add(gear);

        // System date / time in the panel's top-left (the "settings row"),
        // year-month-day and time all on one line.
        var clockTime = new TextBlock
        {
            FontSize = Math.Max(15, icon * 0.3),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = 0.55,
                Color = Color.FromRgb(0x06, 0x0B, 0x16),
            },
        };
        Canvas.SetLeft(clockTime, left + 18);
        Canvas.SetTop(clockTime, top + 14);
        Panel.SetZIndex(clockTime, 2000);
        PanelCanvas.Children.Add(clockTime);
        _glassClockTime = clockTime;
        _glassClockDate = null;
        UpdateGlassClock();
    }

    /// <summary>Draws an Apple-style "Liquid Glass" slab: a clear, lightly
    /// tinted translucent body with a bright luminous edge rim (the signature
    /// refractive lens edge), a soft top specular dome, a faint diagonal glare
    /// and a gentle base shade for depth. Shared by the main grid panel and the
    /// taskbar-row backdrop so they look identical. When <paramref name="track"/>
    /// is supplied, every created element is also added to it so the caller can
    /// remove the slab later.</summary>
    internal void DrawGlassSlab(double left, double top, double w, double h, double radius, double opacity,
        System.Collections.Generic.List<FrameworkElement>? track = null)
    {
        // Body: a clear, lightly cool-tinted sheet of glass. Much more
        // transparent than a frosted panel so the content behind reads through,
        // with only a whisper of top-to-bottom lighting. A soft, wide drop
        // shadow lets the slab float like Apple's Liquid Glass.
        var glass = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x24, 0xEA, 0xF2, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x30, 0xCE, 0xDC, 0xF2), 1.0),
                },
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 48,
                ShadowDepth = 10,
                Direction = 270,
                Opacity = 0.42,
                Color = Color.FromRgb(0x06, 0x0B, 0x16),
            },
            IsHitTestVisible = false,
            CacheMode = new System.Windows.Media.BitmapCache(),
        };
        Canvas.SetLeft(glass, left);
        Canvas.SetTop(glass, top);
        Panel.SetZIndex(glass, -12);
        PanelCanvas.Children.Add(glass);
        track?.Add(glass);

        // Base shade: a soft dark pool at the bottom to give the clear body
        // volume without making it look milky.
        var baseShade = new Border
        {
            Width = w,
            Height = h * 0.4,
            CornerRadius = new CornerRadius(0, 0, radius, radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x0A, 0x12, 0x20), 0.0),
                    new GradientStop(Color.FromArgb(0x30, 0x0A, 0x12, 0x20), 1.0),
                },
            },
        };
        Canvas.SetLeft(baseShade, left);
        Canvas.SetTop(baseShade, top + h * 0.6);
        Panel.SetZIndex(baseShade, -11);
        PanelCanvas.Children.Add(baseShade);
        track?.Add(baseShade);

        // Top specular dome: a soft bright highlight hugging the upper edge,
        // heavily blurred so it reads as light pooling on the curved glass.
        var topCap = new Border
        {
            Width = w * 0.86,
            Height = h * 0.55,
            CornerRadius = new CornerRadius(w * 0.43),
            Opacity = opacity,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(22, h * 0.07) },
            CacheMode = new System.Windows.Media.BitmapCache(),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.12),
                Center = new Point(0.5, 0.12),
                RadiusX = 0.62,
                RadiusY = 0.95,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(topCap, left + w * 0.07);
        Canvas.SetTop(topCap, top + 2);
        Panel.SetZIndex(topCap, -9);
        PanelCanvas.Children.Add(topCap);
        track?.Add(topCap);

        // Diagonal glare streak: a faint tilted bright bar clipped to the panel,
        // the subtle moving-light quality of a glossy glass surface.
        var glareClip = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius),
            CacheMode = new System.Windows.Media.BitmapCache(),
        };
        var glareCanvas = new Canvas { Width = w, Height = h };
        var glare = new System.Windows.Shapes.Rectangle
        {
            Width = w * 1.7,
            Height = h * 0.16,
            RadiusX = h * 0.08,
            RadiusY = h * 0.08,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(18, h * 0.05) },
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-20),
        };
        Canvas.SetLeft(glare, -w * 0.35);
        Canvas.SetTop(glare, h * 0.06);
        glareCanvas.Children.Add(glare);
        glareClip.Child = glareCanvas;
        Canvas.SetLeft(glareClip, left);
        Canvas.SetTop(glareClip, top);
        Panel.SetZIndex(glareClip, -8);
        PanelCanvas.Children.Add(glareClip);
        track?.Add(glareClip);

        // Luminous edge rim — the hallmark of Liquid Glass. A bright, crisp
        // hairline that catches light all the way around the slab, brightest at
        // the top-left where the virtual light source sits, fading along the
        // bottom-right. Sits ON TOP of every other layer so the edge reads sharp.
        var rim = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(1.1),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), 0.4),
                    new GradientStop(Color.FromArgb(0x30, 0xC8, 0xDA, 0xF5), 0.62),
                    new GradientStop(Color.FromArgb(0x9C, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(rim, left);
        Canvas.SetTop(rim, top);
        Panel.SetZIndex(rim, -6);
        PanelCanvas.Children.Add(rim);
        track?.Add(rim);

        // Inner refraction glow: a soft bright ring just inside the rim that
        // mimics the way Liquid Glass magnifies and brightens light at its edge.
        var innerGlow = new Border
        {
            Width = w - 2,
            Height = h - 2,
            CornerRadius = new CornerRadius(radius - 1),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(2.2),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 },
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x2A, 0xDC, 0xEA, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(innerGlow, left + 1);
        Canvas.SetTop(innerGlow, top + 1);
        Panel.SetZIndex(innerGlow, -7);
        PanelCanvas.Children.Add(innerGlow);
        track?.Add(innerGlow);
    }
}

