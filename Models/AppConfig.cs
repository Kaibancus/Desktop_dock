using System.Collections.Generic;

namespace Polaris.Models;

/// <summary>
/// Root configuration object persisted to disk as JSON.
/// </summary>
public sealed class AppConfig
{
    public AppSettings Settings { get; set; } = new();

    /// <summary>Ordered list of apps; list order == ring order.</summary>
    public List<AppEntry> Apps { get; set; } = new();
}
