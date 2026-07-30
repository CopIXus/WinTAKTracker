using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WinTAKTracker.Services.Gps;

/// <summary>
/// Approximate IP geolocation fallback (browser-style), used only when NMEA / Windows Location have no fix.
/// Provider: ipwho.is over HTTPS (no API key). Accuracy is city/region scale — never treated as precision GPS.
/// </summary>
public sealed class NetworkIpGeolocationGps : IDisposable
{
    /// <summary>Default horizontal CE for IP-based fixes (meters). Intentionally large.</summary>
    public const double DefaultAccuracyMeters = 25_000;

    private static readonly Uri Endpoint = new("https://ipwho.is/");
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _running;
    private bool _refreshing;

    public NetworkIpGeolocationGps()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTAKTracker/0.1");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public event EventHandler<GpsFix>? FixReceived;
    public event EventHandler<string>? ErrorOccurred;

    public GpsFix? LastFix { get; private set; }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _ = RefreshAsync();
        _timer = new System.Threading.Timer(_ => _ = RefreshAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public void Stop()
    {
        _running = false;
        _timer?.Dispose();
        _timer = null;
    }

    public async Task RefreshAsync()
    {
        lock (_gate)
        {
            if (!_running || _refreshing) return;
            _refreshing = true;
        }

        try
        {
            using var resp = await _http.GetAsync(Endpoint);
            if (!resp.IsSuccessStatusCode)
            {
                ErrorOccurred?.Invoke(this, $"IP geolocation HTTP {(int)resp.StatusCode}");
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successEl) &&
                successEl.ValueKind == JsonValueKind.False)
            {
                var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "lookup failed";
                ErrorOccurred?.Invoke(this, $"IP geolocation: {msg}");
                return;
            }

            if (!TryGetDouble(root, "latitude", out var lat) ||
                !TryGetDouble(root, "longitude", out var lon))
            {
                ErrorOccurred?.Invoke(this, "IP geolocation response missing coordinates.");
                return;
            }

            if (lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                ErrorOccurred?.Invoke(this, "IP geolocation returned invalid coordinates.");
                return;
            }

            var fix = new GpsFix
            {
                Latitude = lat,
                Longitude = lon,
                AccuracyMeters = DefaultAccuracyMeters,
                Timestamp = DateTimeOffset.UtcNow,
                Source = GpsSourceKind.NetworkIp,
            };
            LastFix = fix;
            FixReceived?.Invoke(this, fix);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
        }
        finally
        {
            lock (_gate) _refreshing = false;
        }
    }

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
