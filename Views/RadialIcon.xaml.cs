using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Polaris.Models;
using Polaris.Services;

namespace Polaris.Views;

public partial class RadialIcon : UserControl
{
    private static readonly Duration Anim = new(TimeSpan.FromMilliseconds(110));
    private const double HoverScale = 1.7;
    private const double LabelWidth = 150;

    public AppEntry Entry { get; }

    /// <summary>Raised when the pointer enters / leaves so the parent ring can
    /// nudge the neighbouring icons aside.</summary>
    public event Action<RadialIcon>? HoverStarted;
    public event Action<RadialIcon>? HoverEnded;

    /// <summary>Raised after the user clicks one of the window previews, so the
    /// host panel can dismiss itself.</summary>
    public event Action? WindowActivated;

    public RadialIcon(AppEntry entry, BitmapSource? icon, double iconSize, Color glowColor, Brush labelBrush)
    {
        Entry = entry;
        IconImage = icon;
        IconSize = iconSize;
        GlowColor = glowColor;
        LabelBrush = labelBrush;
        DisplayName = entry.Name;
        InitializeComponent();

        // Centre the (zero-layout) name label below the icon.
        LabelChrome.Width = LabelWidth;
        Canvas.SetLeft(LabelChrome, (iconSize - LabelWidth) / 2.0);
        Canvas.SetTop(LabelChrome, iconSize + 8);

        // Multi-window hover-thumbnail popup. File Explorer is worth previewing
        // even with a single window (the user often has just one Explorer window
        // with several tabs); every other app needs at least two.
        int minWindows = IsFileExplorer(entry.Path, entry.Arguments) ? 1 : 2;
        _preview = new WindowPreviewPopup(
            this,
            () => WindowPreviewService.GetWindowsForEntry(Entry.Path, Entry.Arguments),
            minWindows,
            onActivated: () => WindowActivated?.Invoke());

        MouseEnter += OnEnter;
        MouseLeave += OnLeave;
        Unloaded += (_, _) => _preview.Close();
    }

    public BitmapSource? IconImage { get; }
    public double IconSize { get; }
    public Color GlowColor { get; }
    public Brush LabelBrush { get; }
    public string DisplayName { get; }

    private bool _isRunning;

    /// <summary>
    /// When true the icon shows a flowing blue light around its square border,
    /// indicating the target program is currently running.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value)
                return;
            _isRunning = value;
            UpdateRunningVisual();
        }
    }

    private void UpdateRunningVisual()
    {
        if (_isRunning)
        {
            RunningBorder.Visibility = Visibility.Visible;
            RunningGlowBorder.Visibility = Visibility.Visible;
            // Sweep the bright spot continuously around the border. A linear
            // 0..360 rotation on the brush is GPU-composited, so it stays smooth.
            // Cap at the display's real refresh: a 4.2 s rotation gains nothing
            // from the global 2x oversampling, so the cap just saves cycles.
            var sweep = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(4.2)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(sweep, App.AmbientFrameRate);
            RunningSweep.BeginAnimation(RotateTransform.AngleProperty, sweep);
            // Gentle breathing glow on the static blurred border (Opacity is a
            // cheap, composited property — no per-frame bitmap-effect recompute).
            var pulse = new DoubleAnimation(0.35, 0.8, new Duration(TimeSpan.FromSeconds(2.2)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            Timeline.SetDesiredFrameRate(pulse, App.AmbientFrameRate);
            RunningGlowBorder.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            RunningSweep.BeginAnimation(RotateTransform.AngleProperty, null);
            RunningGlowBorder.BeginAnimation(OpacityProperty, null);
            RunningBorder.Visibility = Visibility.Collapsed;
            RunningGlowBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void OnEnter(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(HoverScale, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(HoverScale, Anim));
        // Fade in the water-droplet lens (Opacity is composited; the lens blurs
        // are rasterised once via BitmapCache, so no per-frame effect recompute).
        HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, Anim));
        LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Anim));
        HoverStarted?.Invoke(this);

        // Schedule the multi-window preview after a short hover dwell.
        _preview.OnPointerEnter();
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, Anim));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, Anim));
        HoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        LabelChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Anim));
        HoverEnded?.Invoke(this);

        _preview.OnPointerLeave();
    }

    // ---- Multi-window preview popup --------------------------------------

    private readonly WindowPreviewPopup _preview;

    /// <summary>True when the app entry points at the genuine Windows File
    /// Explorer (explorer.exe with NO shell:AppsFolder launcher argument), which
    /// we preview even with a single open window. Packaged apps such as the new
    /// Teams / Outlook are also launched via explorer.exe but with a
    /// shell:AppsFolder argument — those are NOT File Explorer.</summary>
    private static bool IsFileExplorer(string path, string? arguments)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(path), "explorer.exe",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            return WindowPreviewService.TryGetLauncherAumid(path, arguments) == null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Closes the preview popup if it is open (called by the host panel
    /// when it hides).</summary>
    public void ClosePreview() => _preview.Close();
}

