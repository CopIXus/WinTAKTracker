using System.Diagnostics;

namespace WinTAKTracker.Services.Video;

public static class FfmpegLocator
{
    public const string DownloadBuildsUrl = "https://www.gyan.dev/ffmpeg/builds/";
    public const string WingetPackageHint = "winget install \"FFmpeg (Essentials Build)\"";

    // "where ffmpeg" spawns a process (up to 3s); cache the result so repeated
    // Resolve calls (Settings rebuilds, device enumeration) stay cheap.
    private sealed record ResolveCacheEntry(string Key, string? Path, long TimestampMs);

    private const int NotFoundTtlMs = 10_000;
    private static ResolveCacheEntry? _resolveCache;

    public static string? Resolve(string? configuredPath)
    {
        var key = configuredPath ?? "";
        var cached = Volatile.Read(ref _resolveCache);
        if (cached is not null && cached.Key == key)
        {
            if (cached.Path is not null && File.Exists(cached.Path))
                return cached.Path;
            if (cached.Path is null && Environment.TickCount64 - cached.TimestampMs < NotFoundTtlMs)
                return null;
        }

        var resolved = ResolveUncached(configuredPath);
        Volatile.Write(ref _resolveCache, new ResolveCacheEntry(key, resolved, Environment.TickCount64));
        return resolved;
    }

    private static string? ResolveUncached(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        foreach (var dir in CandidateDirectories())
        {
            var beside = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(beside)) return beside;
        }

        var tools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinTAKTracker", "tools", "ffmpeg.exe");
        if (File.Exists(tools)) return tools;

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

    public static string DescribeStatus(string? configuredPath)
    {
        var path = Resolve(configuredPath);
        if (path is null)
            return "not found — required for encode/stream/record";
        return "found — " + path;
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        var process = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process))
        {
            var dir = Path.GetDirectoryName(process);
            if (!string.IsNullOrWhiteSpace(dir))
                yield return dir;
        }

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            yield return AppContext.BaseDirectory;
    }
}
