using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace WinTAKTracker.Services.Video;

public sealed record CameraDevice(int Index, string Name, string? AlternativeName = null);

/// <summary>One completed device scan: what to show and whether FFmpeg listing worked.</summary>
public sealed record CameraSnapshot(
    IReadOnlyList<CameraDevice> Devices,
    bool UsedOpenCvFallback,
    long TimestampMs)
{
    public bool IsFresh => Environment.TickCount64 - TimestampMs < CameraEnumerator.SnapshotTtlMs;
}

public static class CameraEnumerator
{
    private static readonly Regex QuotedName = new("\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex TypedDevice = new(
        "\"([^\"]+)\"\\s*\\((video|audio)\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CameraIndexLabel = new(
        @"^Camera\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Enumeration is expensive: FFmpeg -list_devices spawns a process (seconds),
    // and the OpenCV fallback probes 8 capture indices (much worse). Cache one
    // scan and share it across ListDevices / Resolve* / UsedOpenCvFallback so a
    // stream start or a Settings rebuild costs at most one scan.
    internal const int SnapshotTtlMs = 10_000;
    private const int OpenCvTtlMs = 30_000;

    private sealed record FfmpegScan(string Key, string Stderr, IReadOnlyList<CameraDevice> Devices, long TimestampMs);

    private sealed record OpenCvScan(IReadOnlyList<CameraDevice> Devices, long TimestampMs);

    private static readonly object FfmpegRunGate = new();
    private static readonly object OpenCvRunGate = new();
    private static readonly object InflightGate = new();
    private static FfmpegScan? _ffmpegScan;
    private static OpenCvScan? _openCvScan;
    private static string? _inflightKey;
    private static Task<CameraSnapshot>? _inflight;

    /// <summary>
    /// Prefer FFmpeg DirectShow friendly names; fall back to OpenCvSharp index probes.
    /// </summary>
    public static IReadOnlyList<CameraDevice> ListDevices(string? ffmpegPath = null) =>
        GetSnapshot(ffmpegPath).Devices;

    public static bool UsedOpenCvFallback(string? ffmpegPath = null) =>
        GetSnapshot(ffmpegPath).UsedOpenCvFallback;

    /// <summary>Blocking scan (cached). Safe on background threads; avoid on the UI thread.</summary>
    public static CameraSnapshot GetSnapshot(string? ffmpegPath = null)
    {
        var scan = GetFfmpegScan(ffmpegPath);
        if (scan.Devices.Count > 0)
            return new CameraSnapshot(scan.Devices, UsedOpenCvFallback: false, scan.TimestampMs);
        return new CameraSnapshot(GetOpenCvDevices(), UsedOpenCvFallback: true, scan.TimestampMs);
    }

    /// <summary>Last completed scan for this FFmpeg path (any age), without triggering a new one.</summary>
    public static CameraSnapshot? TryGetCachedSnapshot(string? ffmpegPath = null)
    {
        var key = ffmpegPath ?? "";
        var scan = Volatile.Read(ref _ffmpegScan);
        if (scan is null || scan.Key != key) return null;
        if (scan.Devices.Count > 0)
            return new CameraSnapshot(scan.Devices, UsedOpenCvFallback: false, scan.TimestampMs);
        var cv = Volatile.Read(ref _openCvScan);
        if (cv is null) return null;
        return new CameraSnapshot(cv.Devices, UsedOpenCvFallback: true, scan.TimestampMs);
    }

    /// <summary>Thread-pool scan with single-flight: concurrent callers share one run.</summary>
    public static Task<CameraSnapshot> GetSnapshotAsync(string? ffmpegPath = null)
    {
        if (TryGetCachedSnapshot(ffmpegPath) is { IsFresh: true } fresh)
            return Task.FromResult(fresh);

        var key = ffmpegPath ?? "";
        lock (InflightGate)
        {
            if (_inflight is { } running && _inflightKey == key)
                return running;

            _inflightKey = key;
            var task = Task.Run(() => GetSnapshot(ffmpegPath));
            _inflight = task;
            _ = task.ContinueWith(
                _ =>
                {
                    lock (InflightGate)
                    {
                        if (ReferenceEquals(_inflight, task)) _inflight = null;
                    }
                },
                TaskScheduler.Default);
            return task;
        }
    }

    public static int ResolveIndex(string? cameraName, string? ffmpegPath = null)
    {
        if (string.IsNullOrWhiteSpace(cameraName)) return 0;
        if (int.TryParse(cameraName.Trim(), out var idx)) return idx;
        var m = CameraIndexLabel.Match(cameraName.Trim());
        if (m.Success && int.TryParse(m.Groups[1].Value, out idx)) return idx;

        var devices = ListDevices(ffmpegPath);
        var match = FindDevice(devices, cameraName, allowIndexFallback: false);
        return match?.Index ?? 0;
    }

    /// <summary>DirectShow device string for FFmpeg <c>-i video=…</c> (friendly name, not "Camera 0").</summary>
    public static string ResolveDshowVideoName(string? cameraName, string? ffmpegPath = null)
    {
        var ffmpegDevices = GetFfmpegScan(ffmpegPath).Devices;
        if (ffmpegDevices.Count > 0)
        {
            var match = FindDevice(ffmpegDevices, cameraName, allowIndexFallback: true);
            if (match is not null) return match.Name;
            return ffmpegDevices[0].Name;
        }

        // OpenCV-only listing — "Camera N" is not a valid dshow friendly name for FFmpeg.
        if (string.IsNullOrWhiteSpace(cameraName))
            return "0";
        return cameraName.Trim();
    }

    /// <summary>FFmpeg alternative name (<c>@device_…</c>) when available.</summary>
    public static string? ResolveDshowVideoAlternativeName(string? cameraName, string? ffmpegPath = null)
    {
        var devices = GetFfmpegScan(ffmpegPath).Devices;
        var match = FindDevice(devices, cameraName, allowIndexFallback: true);
        return match?.AlternativeName;
    }

    public static string? ResolveDshowAudioName(string? ffmpegPath = null) =>
        ParseFirstAudio(GetFfmpegScan(ffmpegPath).Stderr);

    /// <summary>True when <paramref name="cameraName"/> is an OpenCV-style label FFmpeg cannot open directly.</summary>
    public static bool IsOpenCvStyleLabel(string? cameraName)
    {
        if (string.IsNullOrWhiteSpace(cameraName)) return false;
        var t = cameraName.Trim();
        return int.TryParse(t, out _) || CameraIndexLabel.IsMatch(t);
    }

    private static CameraDevice? FindDevice(
        IReadOnlyList<CameraDevice> devices,
        string? cameraName,
        bool allowIndexFallback)
    {
        if (devices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(cameraName))
            return allowIndexFallback ? devices[0] : null;

        var trimmed = cameraName.Trim();
        if (int.TryParse(trimmed, out var idx) ||
            (CameraIndexLabel.Match(trimmed) is { Success: true } m && int.TryParse(m.Groups[1].Value, out idx)))
        {
            var byIndex = devices.FirstOrDefault(d => d.Index == idx);
            if (byIndex is not null) return byIndex;
            return allowIndexFallback ? devices[0] : null;
        }

        var exact = devices.FirstOrDefault(d =>
            string.Equals(d.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.AlternativeName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return devices.FirstOrDefault(d =>
            d.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static FfmpegScan GetFfmpegScan(string? ffmpegPath)
    {
        var key = ffmpegPath ?? "";
        var cached = Volatile.Read(ref _ffmpegScan);
        if (cached is not null && cached.Key == key &&
            Environment.TickCount64 - cached.TimestampMs < SnapshotTtlMs)
            return cached;

        lock (FfmpegRunGate)
        {
            cached = Volatile.Read(ref _ffmpegScan);
            if (cached is not null && cached.Key == key &&
                Environment.TickCount64 - cached.TimestampMs < SnapshotTtlMs)
                return cached;

            var stderr = "";
            var ffmpeg = FfmpegLocator.Resolve(ffmpegPath);
            if (ffmpeg is not null)
            {
                try { stderr = RunListDevices(ffmpeg); }
                catch { stderr = ""; }
            }

            var list = new List<CameraDevice>();
            foreach (var (name, alt) in ParseVideoDevices(stderr))
                list.Add(new CameraDevice(list.Count, name, alt));

            var scan = new FfmpegScan(key, stderr, list, Environment.TickCount64);
            Volatile.Write(ref _ffmpegScan, scan);
            return scan;
        }
    }

    private static IReadOnlyList<CameraDevice> GetOpenCvDevices()
    {
        var cached = Volatile.Read(ref _openCvScan);
        if (cached is not null && Environment.TickCount64 - cached.TimestampMs < OpenCvTtlMs)
            return cached.Devices;

        lock (OpenCvRunGate)
        {
            cached = Volatile.Read(ref _openCvScan);
            if (cached is not null && Environment.TickCount64 - cached.TimestampMs < OpenCvTtlMs)
                return cached.Devices;

            var list = ListViaOpenCv();
            Volatile.Write(ref _openCvScan, new OpenCvScan(list, Environment.TickCount64));
            return list;
        }
    }

    private static string RunListDevices(string ffmpeg)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return "";
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(8000);
        return err;
    }

    private static List<CameraDevice> ListViaOpenCv()
    {
        var list = new List<CameraDevice>();
        for (var i = 0; i < 8; i++)
        {
            try
            {
                using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                if (!cap.IsOpened()) continue;
                list.Add(new CameraDevice(i, $"Camera {i}"));
                cap.Release();
            }
            catch
            {
                // skip
            }
        }

        if (list.Count == 0)
            list.Add(new CameraDevice(0, "Camera 0 (default)"));
        return list;
    }

    /// <summary>
    /// Supports classic dshow banners and newer FFmpeg lines like
    /// <c>"Name" (video)</c> / <c>Alternative name "…"</c>.
    /// </summary>
    internal static IEnumerable<(string Name, string? Alt)> ParseVideoDevices(string stderr)
    {
        var inLegacyVideo = false;
        string? pendingName = null;

        foreach (var raw in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw;

            var typed = TypedDevice.Match(line);
            if (typed.Success)
            {
                if (pendingName is not null)
                {
                    yield return (pendingName, null);
                    pendingName = null;
                }

                if (typed.Groups[2].Value.Equals("video", StringComparison.OrdinalIgnoreCase))
                    pendingName = typed.Groups[1].Value;
                // audio lines end the pending video without an alt name
                continue;
            }

            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains("video devices", StringComparison.OrdinalIgnoreCase) &&
                 !line.Contains("(video)", StringComparison.OrdinalIgnoreCase)))
            {
                if (pendingName is not null)
                {
                    yield return (pendingName, null);
                    pendingName = null;
                }

                inLegacyVideo = true;
                continue;
            }

            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains("audio devices", StringComparison.OrdinalIgnoreCase) &&
                 !line.Contains("(audio)", StringComparison.OrdinalIgnoreCase)))
            {
                if (pendingName is not null)
                    yield return (pendingName, null);
                yield break;
            }

            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
            {
                var alt = QuotedName.Match(line);
                if (pendingName is not null && alt.Success)
                {
                    yield return (pendingName, alt.Groups[1].Value);
                    pendingName = null;
                }

                continue;
            }

            if (!inLegacyVideo) continue;
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;

            var m = QuotedName.Match(line);
            if (!m.Success) continue;
            if (pendingName is not null)
                yield return (pendingName, null);
            pendingName = m.Groups[1].Value;
        }

        if (pendingName is not null)
            yield return (pendingName, null);
    }

    private static string? ParseFirstAudio(string stderr)
    {
        foreach (var raw in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var typed = TypedDevice.Match(raw);
            if (typed.Success &&
                typed.Groups[2].Value.Equals("audio", StringComparison.OrdinalIgnoreCase))
                return typed.Groups[1].Value;
        }

        var inAudio = false;
        foreach (var raw in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw;
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains("audio devices", StringComparison.OrdinalIgnoreCase) &&
                 !line.Contains("(audio)", StringComparison.OrdinalIgnoreCase)))
            {
                inAudio = true;
                continue;
            }

            if (!inAudio) continue;
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;
            var m = QuotedName.Match(line);
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }
}
