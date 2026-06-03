using System.Collections.Generic;

namespace DesktopPanel.Models;

/// <summary>
/// User-configurable appearance and behavior settings.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Panel background opacity, 0.0 - 1.0.</summary>
    public double PanelOpacity { get; set; } = 0.85;

    /// <summary>Panel background color (hex, e.g. "#1E1E1E").</summary>
    public string PanelColor { get; set; } = "#1E1E1E";

    /// <summary>Accent / ring color (hex).</summary>
    public string AccentColor { get; set; } = "#3D7EFF";

    /// <summary>Icon label font color (hex).</summary>
    public string FontColor { get; set; } = "#FFFFFF";

    /// <summary>Whether to launch on Windows startup.</summary>
    public bool RunAtStartup { get; set; } = false;

    /// <summary>Diameter of a single icon in device-independent pixels.</summary>
    public double IconSize { get; set; } = 56;

    /// <summary>Maximum icons allowed on a single ring.</summary>
    public int MaxIconsPerRing { get; set; } = 13;

    /// <summary>
    /// Virtual-key code of the hold-to-show trigger key. Default 0xA5 (Right Alt).
    /// </summary>
    public int TriggerKey { get; set; } = 0xA5;
}
