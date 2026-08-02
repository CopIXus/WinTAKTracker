using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace WinTAKTracker.Services.Video;

public sealed record CameraDevice(int Index, string Name, string? AlternativeName = null);

public static class CameraEnumerator
{
    private static readonly Regex QuotedName = new("\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex CameraIndexLabel = new(@"^Camera\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Prefer FFmpeg DirectShow friendly names; fall back to OpenCvSharp index probes.
    /// </summary>
    public static IReadOnlyList<CameraDevice> ListDevices(string? ffmpegPath = null)
    {
        var fromFfmpeg = ListViaFfmpeg(ffmpegPath);
        if (fromFfmpeg.Count > 0) return fromFfmpeg;
        return ListViaOpenCv();
    }

    public static int ResolveIndex(string? cameraName, string? ffmpegPath = null)
    {
        if (string.IsNullOrWhiteSpace(cameraName)) return 0;
        if (int.TryParse(cameraName.Trim(), out var idx)) return idx;
        var m = CameraIndexLabel.Match(cameraName.Trim());
        if (m.Success && int.TryParse(m.Groups[1].Value, out idx)) return idx;

        var devices = ListDevices(ffmpegPath);
        var match = FindDevice(devices, cameraName);
        return match?.Index ?? 0;
    }

    /// <summary>DirectShow device string for FFmpeg <c>-i video="…"</c> (friendly name, not "Camera 0").</summary>
    public static string ResolveDshowVideoName(string? cameraName, string? ffmpegPath = null)
    {
        var devices = ListDevices(ffmpegPath);
        if (devices.Count == 0) return cameraName?.Trim() ?? "0";

        var match = FindDevice(devices, cameraName);
        return match?.Name ?? devices[0].Name;
    }

    /// <summary>FFmpeg alternative name (<c>@device_…</c>) when available — often more reliable than the friendly name.</summary>
    public static string? ResolveDshowVideoAlternativeName(string? cameraName, string? ffmpegPath = null)
    {
        var devices = ListDevices(ffmpegPath);
        var match = FindDevice(devices, cameraName);
        return match?.AlternativeName;
    }

    public static string? ResolveDshowAudioName(string? ffmpegPath = null)
    {
        var ffmpeg = FfmpegLocator.Resolve(ffmpegPath);
        if (ffmpeg is null) return null;
        try
        {
            var err = RunListDevices(ffmpeg);
            return ParseFirstAudio(err);
        }
        catch
        {
            return null;
        }
    }

    private static CameraDevice? FindDevice(IReadOnlyList<CameraDevice> devices, string? cameraName)
    {
        if (devices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(cameraName)) return devices[0];

        var trimmed = cameraName.Trim();
        if (int.TryParse(trimmed, out var idx) ||
            (CameraIndexLabel.Match(trimmed) is { Success: true } m && int.TryParse(m.Groups[1].Value, out idx)))
        {
            var byIndex = devices.FirstOrDefault(d => d.Index == idx);
            if (byIndex is not null) return byIndex;
        }

        var exact = devices.FirstOrDefault(d =>
            string.Equals(d.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.AlternativeName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var partial = devices.FirstOrDefault(d =>
            d.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains(d.Name, StringComparison.OrdinalIgnoreCase));
        return partial ?? devices[0];
    }

    private static List<CameraDevice> ListViaFfmpeg(string? ffmpegPath)
    {
        var list = new List<CameraDevice>();
        var ffmpeg = FfmpegLocator.Resolve(ffmpegPath);
        if (ffmpeg is null) return list;
        try
        {
            var err = RunListDevices(ffmpeg);
            foreach (var (name, alt) in ParseVideoDevices(err))
                list.Add(new CameraDevice(list.Count, name, alt));
        }
        catch
        {
            // fall through
        }

        return list;
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

    private static IEnumerable<(string Name, string? Alt)> ParseVideoDevices(string stderr)
    {
        var inVideo = false;
        string? pendingName = null;
        foreach (var raw in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw;
            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("video devices", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingName is not null)
                {
                    yield return (pendingName, null);
                    pendingName = null;
                }

                inVideo = true;
                continue;
            }

            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("audio devices", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingName is not null)
                    yield return (pendingName, null);
                yield break;
            }

            if (!inVideo) continue;

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
        var inAudio = false;
        foreach (var raw in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw;
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("audio devices", StringComparison.OrdinalIgnoreCase))
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
