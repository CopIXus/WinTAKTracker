using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace WinTAKTracker.Services.Video;

public sealed record CameraDevice(int Index, string Name, string? AlternativeName = null);

public static class CameraEnumerator
{
    private static readonly Regex QuotedName = new("\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex TypedDevice = new(
        "\"([^\"]+)\"\\s*\\((video|audio)\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CameraIndexLabel = new(
        @"^Camera\s+(\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Prefer FFmpeg DirectShow friendly names; fall back to OpenCvSharp index probes.
    /// </summary>
    public static IReadOnlyList<CameraDevice> ListDevices(string? ffmpegPath = null)
    {
        var fromFfmpeg = ListViaFfmpeg(ffmpegPath);
        if (fromFfmpeg.Count > 0) return fromFfmpeg;
        return ListViaOpenCv();
    }

    public static bool UsedOpenCvFallback(string? ffmpegPath = null) =>
        ListViaFfmpeg(ffmpegPath).Count == 0;

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
        var ffmpegDevices = ListViaFfmpeg(ffmpegPath);
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
        var devices = ListViaFfmpeg(ffmpegPath);
        var match = FindDevice(devices, cameraName, allowIndexFallback: true);
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
