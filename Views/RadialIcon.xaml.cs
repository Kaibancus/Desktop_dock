using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DesktopPanel.Models;

namespace DesktopPanel.Views;

public partial class RadialIcon : UserControl
{
    private static readonly Duration Anim = new(TimeSpan.FromMilliseconds(150));

    public AppEntry Entry { get; }

    public RadialIcon(AppEntry entry, BitmapSource? icon, double iconSize, Color glowColor, Brush labelBrush)
    {
        Entry = entry;
        IconImage = icon;
        IconSize = iconSize;
        GlowColor = glowColor;
        LabelBrush = labelBrush;
        DisplayName = entry.Name;
        InitializeComponent();

        MouseEnter += OnEnter;
        MouseLeave += OnLeave;
    }

    public BitmapSource? IconImage { get; }
    public double IconSize { get; }
    public Color GlowColor { get; }
    public Brush LabelBrush { get; }
    public string DisplayName { get; }

    private void OnEnter(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.3, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.3, Anim));
        Glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(24, Anim));
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, Anim));
        Glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(0, Anim));
    }
}
