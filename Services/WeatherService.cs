using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Polaris.Services;

/// <summary>
/// Fetches the current weather + city for the dock's clock line, key-free:
/// location is resolved by IP (ip-api, localized to Chinese city names) and the
/// weather comes from Open-Meteo. The result is cached and refreshed at most
/// every <see cref="RefreshInterval"/>; callers poll <see cref="Summary"/> and
/// subscribe to <see cref="Updated"/>. All failures are swallowed so an offline
/// machine simply keeps showing the clock without weather.
/// </summary>
public sealed class WeatherService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(20);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>The formatted "weather location" line, e.g. "晴 23°  北京", or null
    /// until the first successful fetch.</summary>
    public string? Summary { get; private set; }

    /// <summary>Raised (on a background thread) whenever <see cref="Summary"/>
    /// changes. Handlers must marshal to the UI thread themselves.</summary>
    public event Action? Updated;

    private DateTime _lastFetch = DateTime.MinValue;
    private bool _busy;

    /// <summary>Refreshes the cached weather if it is stale (or <paramref name="force"/>).
    /// Safe to call frequently — it self-throttles and never throws.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (_busy)
            return;
        if (!force && Summary != null && DateTime.Now - _lastFetch < RefreshInterval)
            return;
        _busy = true;
        try
        {
            // 1. Locate by IP (no key); lang=zh-CN gives Chinese place names.
            var locJson = await Http.GetStringAsync(
                "http://ip-api.com/json/?lang=zh-CN&fields=status,city,regionName,lat,lon")
                .ConfigureAwait(false);
            using var locDoc = JsonDocument.Parse(locJson);
            var loc = locDoc.RootElement;
            if (!loc.TryGetProperty("status", out var st) || st.GetString() != "success")
                return;
            double lat = loc.GetProperty("lat").GetDouble();
            double lon = loc.GetProperty("lon").GetDouble();
            string city = loc.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(city) && loc.TryGetProperty("regionName", out var rn))
                city = rn.GetString() ?? "";

            // 2. Current weather (Open-Meteo, no key). Invariant culture so the
            //    decimal point in the coordinates is a dot, not a comma.
            string wUrl = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.0000}&longitude={1:0.0000}&current=temperature_2m,weather_code",
                lat, lon);
            var wJson = await Http.GetStringAsync(wUrl).ConfigureAwait(false);
            using var wDoc = JsonDocument.Parse(wJson);
            var cur = wDoc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();

            string desc = WmoToChinese(code);
            string s = $"{desc} {Math.Round(temp)}°   {city}".Trim();
            if (s != Summary)
            {
                Summary = s;
                Updated?.Invoke();
            }
            _lastFetch = DateTime.Now;
        }
        catch
        {
            // Offline / API hiccup — keep whatever we last had.
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Maps a WMO weather-interpretation code (as used by Open-Meteo) to a
    /// short Chinese description.</summary>
    private static string WmoToChinese(int code) => code switch
    {
        0 => "晴",
        1 => "晴间多云",
        2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        56 or 57 => "冻毛毛雨",
        61 => "小雨",
        63 => "中雨",
        65 => "大雨",
        66 or 67 => "冻雨",
        71 => "小雪",
        73 => "中雪",
        75 => "大雪",
        77 => "雪粒",
        80 => "阵雨",
        81 => "强阵雨",
        82 => "暴雨",
        85 or 86 => "阵雪",
        95 => "雷阵雨",
        96 or 99 => "雷暴冰雹",
        _ => "—",
    };
}
