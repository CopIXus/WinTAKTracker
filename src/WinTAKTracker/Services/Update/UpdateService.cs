using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Update;

public sealed class UpdateCheckResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string CurrentVersion { get; init; } = "0.1.0";
    public string? LatestVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Sha256Url { get; init; }
    public string? Sha256Expected { get; init; }
    public bool UpdateAvailable { get; init; }
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> DownloadAndApplyAsync(UpdateCheckResult check, CancellationToken ct = default);
    string CurrentVersion { get; }
}

/// <summary>GitHub Releases updater for CopIXus/WinTAKTracker.</summary>
public sealed class UpdateService : IUpdateService
{
    private readonly AppConfigStore _store;
    private readonly IRedactedLogger _log;
    private readonly Func<UpdateSettings> _settings;
    private readonly HttpClient _http;

    public UpdateService(AppConfigStore store, IRedactedLogger log, Func<UpdateSettings> settings)
    {
        _store = store;
        _log = log;
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinTAKTracker/0.1");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        try
        {
            var url = _settings().ReleasesApiUrl;
            using var resp = await _http.GetAsync(url, ct);
            if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            {
                return new UpdateCheckResult
                {
                    Success = false,
                    Error = "GitHub rate limit or access denied. Try again later.",
                    CurrentVersion = current,
                };
            }

            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: ct);
            if (release is null)
                return new UpdateCheckResult { Success = false, Error = "Empty release response.", CurrentVersion = current };

            var tag = release.TagName?.TrimStart('v', 'V') ?? "";
            var exeAsset = release.Assets?.FirstOrDefault(a =>
                a.Name is not null && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var shaAsset = release.Assets?.FirstOrDefault(a =>
                a.Name is not null &&
                (a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                 a.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase)));

            string? expectedSha = null;
            if (shaAsset?.BrowserDownloadUrl is not null)
            {
                try
                {
                    var shaText = await _http.GetStringAsync(shaAsset.BrowserDownloadUrl, ct);
                    expectedSha = ExtractSha256(shaText);
                }
                catch { /* optional */ }
            }

            var available = !string.IsNullOrEmpty(tag) && IsNewer(tag, current);

            return new UpdateCheckResult
            {
                Success = true,
                CurrentVersion = current,
                LatestVersion = tag,
                ReleaseNotes = Truncate(release.Body, 800),
                DownloadUrl = exeAsset?.BrowserDownloadUrl,
                Sha256Url = shaAsset?.BrowserDownloadUrl,
                Sha256Expected = expectedSha,
                UpdateAvailable = available && exeAsset?.BrowserDownloadUrl is not null,
            };
        }
        catch (Exception ex)
        {
            _log.Warn("Update", $"Check failed: {ex.GetType().Name}");
            return new UpdateCheckResult
            {
                Success = false,
                Error = $"Update check failed ({ex.GetType().Name}).",
                CurrentVersion = current,
            };
        }
    }

    public async Task<(bool Ok, string Message)> DownloadAndApplyAsync(UpdateCheckResult check, CancellationToken ct = default)
    {
        if (check.DownloadUrl is null)
            return (false, "No download URL.");

        try
        {
            Directory.CreateDirectory(_store.UpdatesDirectory);
            var dest = Path.Combine(_store.UpdatesDirectory, "WinTAKTracker.exe");
            await using (var fs = File.Create(dest))
            await using (var stream = await _http.GetStreamAsync(check.DownloadUrl, ct))
                await stream.CopyToAsync(fs, ct);

            if (!string.IsNullOrWhiteSpace(check.Sha256Expected))
            {
                var actual = await ComputeSha256Async(dest);
                if (!actual.Equals(check.Sha256Expected, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(dest); } catch { /* ignore */ }
                    return (false, "SHA256 verification failed. Update aborted.");
                }
            }

            var currentExe = Environment.ProcessPath
                             ?? Path.Combine(AppContext.BaseDirectory, "WinTAKTracker.exe");
            var bat = Path.Combine(_store.UpdatesDirectory, "apply-update.bat");
            var script = $"""
                @echo off
                setlocal
                ping 127.0.0.1 -n 3 >nul
                copy /Y "{dest}" "{currentExe}"
                start "" "{currentExe}"
                del "%~f0"
                """;
            await File.WriteAllTextAsync(bat, script, ct);

            _log.Info("Update", "Update downloaded; scheduling restart swap.");
            Process.Start(new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            });

            return (true, "Update ready. The app will restart.");
        }
        catch (Exception ex)
        {
            _log.Error("Update", "Apply failed", ex);
            return (false, $"Update failed ({ex.GetType().Name}).");
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs);
        return Convert.ToHexString(hash);
    }

    private static string? ExtractSha256(string text)
    {
        var m = Regex.Match(text.Trim(), @"\b[a-fA-F0-9]{64}\b");
        return m.Success ? m.Value : null;
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(Normalize(latest), out var l)) return false;
        if (!Version.TryParse(Normalize(current), out var c)) return true;
        return l > c;
    }

    private static string Normalize(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        var dash = v.IndexOf('-');
        if (dash >= 0) v = v[..dash];
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
            return $"{parts[0]}.{parts[1]}.{parts[2]}";
        if (parts.Length == 2)
            return $"{parts[0]}.{parts[1]}.0";
        if (parts.Length == 1)
            return $"{parts[0]}.0.0";
        return "0.0.0";
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "…";

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
