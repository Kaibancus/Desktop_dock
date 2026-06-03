using System;
using System.IO;
using System.Text.Json;
using DesktopPanel.Models;

namespace DesktopPanel.Services;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> to %AppData%\DesktopPanel\config.json.
/// </summary>
public static class ConfigStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopPanel");

    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (cfg != null)
                {
                    cfg.Settings ??= new AppSettings();
                    cfg.Apps ??= new();
                    return cfg;
                }
            }
        }
        catch
        {
            // Fall through to defaults on any read/parse error.
        }

        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(config, Options);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence; ignore IO failures.
        }
    }
}
