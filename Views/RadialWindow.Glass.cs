using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopPanel.Services;

namespace DesktopPanel.Views;

// Non-Saturn theme rendering: the translucent "liquid glass" panel and the
// minimal centre button used by plain grid themes.
public partial class RadialWindow
{
    /// <summary>A minimal centre control used by non-Saturn themes: a small
    /// circular settings button placed above the grid.</summary>
    private void DrawSimpleCenterButton()
    {
        double s = Math.Max(34, _config.Settings.IconSize * 0.7);
        var btn = new Border
        {
            Width = s,
            Height = s,
            CornerRadius = new CornerRadius(s / 2),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0x60, 0x60)),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = "⚙",
                FontSize = s * 0.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        btn.MouseLeftButtonUp += (_, e) => { e.Handled = true; RequestOpenSettings?.Invoke(); };

        double gridTop = _center.Y - 2 * (_config.Settings.IconSize * 2.1);
        Canvas.SetLeft(btn, _center.X - s / 2);
        Canvas.SetTop(btn, gridTop - s - _config.Settings.IconSize * 0.6);
        Panel.SetZIndex(btn, 2000);
        PanelCanvas.Children.Add(btn);
    }

    /// <summary>
    /// Draws the "液态玻璃" backdrop: a translucent, frosted rounded-rectangle
    /// panel sized to enclose the 4×3 icon grid. Its overall opacity follows the
    /// user's panel-transparency setting.
    /// </summary>
    private void DrawGlassPanel()
    {
        double icon = _config.Settings.IconSize;
        double cellW = icon * 2.15;
        double cellH = icon * 2.35;
        double gridW = (LiquidGlassTheme.Columns - 1) * cellW;
        double gridH = (LiquidGlassTheme.Rows - 1) * cellH;

        double padX = icon * 1.15;
        double padY = icon * 1.15;
        double w = gridW + icon + padX * 2;
        double h = gridH + icon + padY * 2;
        double left = _center.X - w / 2.0;
        double top = _center.Y - h / 2.0;

        double opacity = 1.0 - Math.Clamp(_config.Settings.PanelTransparency, 0.0, 1.0);
        const double radius = 28;

        // Base frosted fill: a convex "lens" gradient that reads as a rounded,
        // three-dimensional slab of glass rather than a flat sheet.
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
                    new GradientStop(Color.FromArgb(0x82, 0xF2, 0xF8, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x5A, 0xDD, 0xEA, 0xFB), 0.18),
                    new GradientStop(Color.FromArgb(0x36, 0xB7, 0xC9, 0xE4), 0.52),
                    new GradientStop(Color.FromArgb(0x4A, 0xC4, 0xD6, 0xEE), 0.84),
                    new GradientStop(Color.FromArgb(0x6E, 0xE3, 0xEF, 0xFF), 1.0),
                },
            },
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
            BorderThickness = new Thickness(1.4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 40,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.5,
                Color = Color.FromRgb(0x0A, 0x10, 0x1C),
            },
            IsHitTestVisible = false,
            CacheMode = new System.Windows.Media.BitmapCache(),
        };
        Canvas.SetLeft(glass, left);
        Canvas.SetTop(glass, top);
        Panel.SetZIndex(glass, -12);
        PanelCanvas.Children.Add(glass);

        // Inner bevel: bright on the top-left, shaded on the bottom-right, which
        // simulates light raking across a raised glass edge and gives depth.
        double bevelInset = 3;
        var bevel = new Border
        {
            Width = w - bevelInset * 2,
            Height = h - bevelInset * 2,
            CornerRadius = new CornerRadius(radius - bevelInset),
            Opacity = opacity,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(1.6),
            BorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0.45),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55),
                    new GradientStop(Color.FromArgb(0x55, 0x20, 0x30, 0x48), 1.0),
                },
            },
        };
        Canvas.SetLeft(bevel, left + bevelInset);
        Canvas.SetTop(bevel, top + bevelInset);
        Panel.SetZIndex(bevel, -8);
        PanelCanvas.Children.Add(bevel);

        // Top specular cap: a bright curved highlight hugging the upper third.
        // The heavy blur dissolves the rectangle edges into a soft dome of light.
        var topCap = new Border
        {
            Width = w * 0.82,
            Height = h * 0.5,
            CornerRadius = new CornerRadius(w * 0.41),
            Opacity = opacity,
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(20, h * 0.06) },
            CacheMode = new System.Windows.Media.BitmapCache(),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.18),
                Center = new Point(0.5, 0.18),
                RadiusX = 0.62,
                RadiusY = 0.95,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
        };
        Canvas.SetLeft(topCap, left + w * 0.09);
        Canvas.SetTop(topCap, top + bevelInset);
        Panel.SetZIndex(topCap, -7);
        PanelCanvas.Children.Add(topCap);

        // Diagonal glare streak: a tilted bright bar clipped to the rounded panel.
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
            Height = h * 0.18,
            RadiusX = h * 0.09,
            RadiusY = h * 0.09,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(16, h * 0.045) },
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                },
            },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-22),
        };
        Canvas.SetLeft(glare, -w * 0.35);
        Canvas.SetTop(glare, h * 0.08);
        glareCanvas.Children.Add(glare);
        glareClip.Child = glareCanvas;
        Canvas.SetLeft(glareClip, left);
        Canvas.SetTop(glareClip, top);
        Panel.SetZIndex(glareClip, -6);
        PanelCanvas.Children.Add(glareClip);

        // Bottom inner shadow: a soft dark gradient pooling at the base.
        var baseShade = new Border
        {
            Width = w,
            Height = h * 0.34,
            CornerRadius = new CornerRadius(0, 0, radius, radius),
            Opacity = opacity,
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x10, 0x1A, 0x2C), 0.0),
                    new GradientStop(Color.FromArgb(0x3A, 0x10, 0x1A, 0x2C), 1.0),
                },
            },
        };
        Canvas.SetLeft(baseShade, left);
        Canvas.SetTop(baseShade, top + h * 0.66);
        Panel.SetZIndex(baseShade, -7);
        PanelCanvas.Children.Add(baseShade);

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
    }
}
