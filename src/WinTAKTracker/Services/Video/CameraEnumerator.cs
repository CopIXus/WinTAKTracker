using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace WinTAKTracker.Services.Video;

public sealed record CameraDevice(int Index, string Name);

public static class CameraEnumerator
{
    private static readonly Regex QuotedName = new("\"([^\"]+)\"", RegexOptions.Compiled);

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
        var devices = ListDevices(ffmpegPath);
        var match = devices.FirstOrDefault(d =>
            string.Equals(d.Name, cameraName, StringComparison.OrdinalIgnoreCase) ||
            d.Name.Contains(cameraName, StringComparison.OrdinalIgnoreCase));
        return match?.Index ?? 0;
    }

    /// <summary>DirectShow device string for FFmpeg <c>-i video="…"</c>.</summary>
    public static string ResolveDshowVideoName(string? cameraName, string? ffmpegPath = null)
    {
        if (string.IsNullOrWhiteSpace(cameraName)) return "0";
        if (int.TryParse(cameraName.Trim(), out var idx))
        {
            var byIndex = ListDevices(ffmpegPath).FirstOrDefault(d => d.Index == idx);
            return byIndex?.Name ?? cameraName.Trim();
        }

        return cameraName.Trim();
    }

    public static string? ResolveDshowAudioName(string? ffmpegPath = null)
    {
        var ffmpeg = FfmpegLocator.Resolve(ffmpegPath);
        if (ffmpeg is null) return null;
        try
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
            if (p is null) return null;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return ParseFirstAudio(err);
        }
        catch
        {
            return null;
        }
    }

    private static List<CameraDevice> ListViaFfmpeg(string? ffmpegPath)
    {
        var list = new List<CameraDevice>();
        var ffmpeg = FfmpegLocator.Resolve(ffmpegPath);
        if (ffmpeg is null) return list;
        try
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
            if (p is null) return list;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            foreach (var name in ParseVideoNames(err))
                list.Add(new CameraDevice(list.Count, name));
        }
        catch
        {
            // fall through
        }

        return list;
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

    private static IEnumerable<string> ParseVideoNames(string stderr)
    {
        var inVideo = false;
        foreach (var raw in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw;
            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("video devices", StringComparison.OrdinalIgnoreCase))
            {
                inVideo = true;
                continue;
            }

            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("audio devices", StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            if (!inVideo) continue;
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase)) continue;
            var m = QuotedName.Match(line);
            if (m.Success)
                yield return m.Groups[1].Value;
        }
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
