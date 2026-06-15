using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace Polaris.Services;

/// <summary>
/// Fetches the current weather + city for the dock's clock line, key-free:
/// the position comes from the native Windows location service
/// (<see cref="Geolocator"/>, no third-party geo-IP lookup), the Chinese city
/// label from a best-effort reverse geocode, and the weather from Open-Meteo.
/// The result is cached and refreshed at most every <see cref="RefreshInterval"/>;
/// callers poll <see cref="Summary"/> and subscribe to <see cref="Updated"/>. All
/// failures are swallowed so a machine with location disabled or no network simply
/// keeps showing the clock without weather.
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
            // 1. Locate via the native Windows location service (no third-party
            //    geo-IP). Yields lat/lon locally; the city label is then filled
            //    in by a best-effort reverse geocode.
            var location = await ResolveLocationAsync().ConfigureAwait(false);
            if (location is not (double lat, double lon, string city))
                return;

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

    /// <summary>Resolves the current position from the native Windows location
    /// service and pairs it with a best-effort Chinese city label. Returns null
    /// (so the caller silently keeps the previous weather) when location access is
    /// off / unavailable. Never throws.</summary>
    private async Task<(double lat, double lon, string city)?> ResolveLocationAsync()
    {
        double lat, lon;
        try
        {
            // Ask the OS for permission first; harmless if already granted. Some
            // environments only allow this from a UI thread, so a failure here is
            // tolerated and we still attempt the position read below.
            try { await Geolocator.RequestAccessAsync(); } catch { }

            var geo = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            // Accept a cached fix up to 10 min old; cap the wait so a stalled
            // provider can't hang the refresh.
            var pos = await geo.GetGeopositionAsync(
                    TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(8))
                .AsTask().ConfigureAwait(false);
            var point = pos.Coordinate.Point.Position;
            lat = point.Latitude;
            lon = point.Longitude;
        }
        catch
        {
            // Location services disabled, denied, or no fix available.
            return null;
        }

        string city = await ReverseGeocodeCityAsync(lat, lon).ConfigureAwait(false);
        return (lat, lon, city);
    }

    /// <summary>Best-effort reverse geocode of the given coordinates to a short
    /// Chinese city name (key-free, via BigDataCloud). Returns an empty string on
    /// any failure — the weather still shows, just without the place label.</summary>
    private static async Task<string> ReverseGeocodeCityAsync(double lat, double lon)
    {
        try
        {
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={0:0.0000}&longitude={1:0.0000}&localityLanguage=zh",
                lat, lon);
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var field in new[] { "city", "locality", "principalSubdivision" })
            {
                if (root.TryGetProperty(field, out var v) &&
                    v.GetString() is { Length: > 0 } name)
                    return name;
            }
        }
        catch
        {
            // No network / blocked — fall back to no city label.
        }
        return "";
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
