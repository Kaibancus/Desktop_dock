using Microsoft.Win32;

namespace Polaris.Services;

/// <summary>Reads the current Windows app appearance (light or dark). Used to
/// style chrome that should match the OS theme — the settings window and the
/// dock's right-click menus.</summary>
public static class SystemTheme
{
    /// <summary>True when Windows is set to the LIGHT app theme. Reads
    /// HKCU\…\Themes\Personalize\AppsUseLightTheme (1 = light, 0 = dark);
    /// defaults to dark when the value is missing or unreadable. Re-read on each
    /// access so a live theme switch is picked up the next time chrome is built.</summary>
    public static bool IsLight
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var v = key?.GetValue("AppsUseLightTheme");
                return v is int i && i != 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
