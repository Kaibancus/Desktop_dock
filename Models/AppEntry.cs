using System.Text.Json.Serialization;

namespace Polaris.Models;

/// <summary>
/// One launchable application entry shown on the radial panel.
/// </summary>
public sealed class AppEntry
{
    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Target executable path (resolved from a .lnk if needed).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Optional command line arguments.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Optional working directory.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Path the icon should be extracted from. Usually equals <see cref="Path"/>,
    /// but a .lnk may point its icon at a different file.
    /// </summary>
    public string IconSource { get; set; } = string.Empty;

    [JsonIgnore]
    public string EffectiveIconSource =>
        string.IsNullOrWhiteSpace(IconSource) ? Path : IconSource;

    /// <summary>
    /// True when <see cref="Path"/> is a shell-namespace token (This PC,
    /// Recycle Bin, etc.) rather than a file-system path.
    /// </summary>
    [JsonIgnore]
    public bool IsShellItem =>
        Path.StartsWith("::{", System.StringComparison.Ordinal) ||
        Path.StartsWith("shell:", System.StringComparison.OrdinalIgnoreCase);
}
