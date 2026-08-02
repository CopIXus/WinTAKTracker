using System.Diagnostics;

namespace WinTAKTracker.Services.Video;

public static class FfmpegLocator
{
    public static string? Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(beside)) return beside;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "ffmpeg",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(3000);
            var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(line) && File.Exists(line.Trim()))
                return line.Trim();
        }
        catch { /* ignore */ }

        return null;
    }

    public static bool IsAvailable(string? configuredPath) => Resolve(configuredPath) is not null;
}
