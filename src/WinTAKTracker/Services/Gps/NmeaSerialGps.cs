using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace WinTAKTracker.Services.Gps;

/// <summary>Reads NMEA from a serial COM port ($GPRMC/$GNRMC + $GPGGA/$GNGGA).</summary>
public sealed class NmeaSerialGps : IDisposable
{
    private readonly object _gate = new();
    private SerialPort? _port;
    private readonly StringBuilder _lineBuf = new();
    private double? _lat, _lon, _alt, _speedMps, _course, _hdop;
    private bool _hasRmcFix;

    public event EventHandler<GpsFix>? FixReceived;
    public event EventHandler<string>? ErrorOccurred;

    public bool IsOpen
    {
        get { lock (_gate) return _port?.IsOpen == true; }
    }

    public string? PortName
    {
        get { lock (_gate) return _port?.PortName; }
    }

    public static string[] GetAvailablePorts() => SerialPort.GetPortNames().OrderBy(p => p).ToArray();

    public void Start(string portName, int baudRate)
    {
        Stop();
        try
        {
            var port = new SerialPort(portName, baudRate)
            {
                NewLine = "\n",
                Encoding = Encoding.ASCII,
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true,
            };
            port.DataReceived += OnDataReceived;
            port.ErrorReceived += (_, e) => ErrorOccurred?.Invoke(this, e.EventType.ToString());
            port.Open();
            lock (_gate) _port = port;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
            throw;
        }
    }

    public void Stop()
    {
        SerialPort? port;
        lock (_gate)
        {
            port = _port;
            _port = null;
        }

        if (port is null) return;
        try
        {
            port.DataReceived -= OnDataReceived;
            if (port.IsOpen) port.Close();
        }
        catch { /* ignore */ }
        port.Dispose();
    }

    private void OnDataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        SerialPort? port;
        lock (_gate) port = _port;
        if (port is null || !port.IsOpen) return;

        try
        {
            var chunk = port.ReadExisting();
            foreach (var ch in chunk)
            {
                if (ch is '\r') continue;
                if (ch is '\n')
                {
                    var line = _lineBuf.ToString().Trim();
                    _lineBuf.Clear();
                    if (line.Length > 0) ProcessSentence(line);
                }
                else
                {
                    _lineBuf.Append(ch);
                    if (_lineBuf.Length > 512) _lineBuf.Clear();
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex.Message);
        }
    }

    private void ProcessSentence(string sentence)
    {
        if (!sentence.StartsWith('$')) return;
        var star = sentence.IndexOf('*');
        var body = star > 0 ? sentence[..star] : sentence;
        var parts = body.Split(',');
        if (parts.Length < 2) return;

        var type = parts[0];
        if (type.EndsWith("RMC", StringComparison.OrdinalIgnoreCase))
            ParseRmc(parts);
        else if (type.EndsWith("GGA", StringComparison.OrdinalIgnoreCase))
            ParseGga(parts);

        if (_hasRmcFix && _lat.HasValue && _lon.HasValue)
        {
            FixReceived?.Invoke(this, new GpsFix
            {
                Latitude = _lat.Value,
                Longitude = _lon.Value,
                AltitudeMeters = _alt,
                SpeedMetersPerSecond = _speedMps,
                CourseDegrees = _course,
                AccuracyMeters = _hdop.HasValue ? _hdop.Value * 5 : null,
                Hdop = _hdop,
                Timestamp = DateTimeOffset.UtcNow,
                Source = GpsSourceKind.NmeaSerial,
            });
        }
    }

    private void ParseRmc(string[] p)
    {
        // $GPRMC,time,status,lat,N,lon,E,speedKnots,course,...
        if (p.Length < 9) return;
        var status = p[2];
        _hasRmcFix = status.Equals("A", StringComparison.OrdinalIgnoreCase);
        if (!_hasRmcFix) return;

        _lat = ParseNmeaCoord(p[3], p[4]);
        _lon = ParseNmeaCoord(p[5], p[6]);
        if (double.TryParse(p[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var knots))
            _speedMps = knots * 0.514444;
        if (p.Length > 8 && double.TryParse(p[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var course))
            _course = course;
    }

    private void ParseGga(string[] p)
    {
        // $GPGGA,time,lat,N,lon,E,fix,sats,hdop,alt,M,...
        if (p.Length < 10) return;
        if (int.TryParse(p[6], out var quality) && quality == 0) return;
        var lat = ParseNmeaCoord(p[2], p[3]);
        var lon = ParseNmeaCoord(p[4], p[5]);
        if (lat.HasValue) _lat = lat;
        if (lon.HasValue) _lon = lon;
        if (double.TryParse(p[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var hdop))
            _hdop = hdop;
        if (double.TryParse(p[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var alt))
            _alt = alt;
        if (_lat.HasValue && _lon.HasValue) _hasRmcFix = true;
    }

    private static double? ParseNmeaCoord(string raw, string hemi)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(hemi)) return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        var deg = Math.Floor(v / 100.0);
        var minutes = v - deg * 100.0;
        var dec = deg + minutes / 60.0;
        if (hemi is "S" or "W" or "s" or "w") dec = -dec;
        return dec;
    }

    public void Dispose() => Stop();
}
