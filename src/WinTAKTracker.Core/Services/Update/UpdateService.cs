using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Update;

public enum UpdateAssetKind
{
    None = 0,
    PortableExe = 1,
    SetupInstaller = 2,
}

public sealed class UpdateCheckResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string CurrentVersion { get; init; } = "0.1.0";
    public string? LatestVersion { get; init; }
    public string? ReleaseNotes { get; init; }

    /// <summary>CHANGELOG.md section for the new version (plain text), when it could be fetched.</summary>
    public string? ChangelogNotes { get; init; }
    public string? DownloadUrl { get; init; }
    public string? AssetName { get; init; }
    public UpdateAssetKind AssetKind { get; init; }
    public bool RequiresElevation { get; init; }
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
    private const string PortableAssetName = "WinTAKTracker.exe";
    private const string SetupAssetName = "WinTAKTracker-Setup.exe";

    private static readonly Regex LeadingSemVer = new(
        @"^(?<ver>\d+(?:\.\d+){0,3})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        var preferSetup = IsManagedInstall();
        try
        {
            var configuredUrl = _settings().ReleasesApiUrl;
            var url = ResolveReleasesListUrl(configuredUrl);
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
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var releases = ParseReleases(doc.RootElement);
            if (releases.Count == 0)
                return new UpdateCheckResult { Success = false, Error = "Empty release response.", CurrentVersion = current };

            var selected = SelectBestRelease(releases, preferSetup);
            if (selected is null)
            {
                var missing = preferSetup ? SetupAssetName : PortableAssetName;
                return new UpdateCheckResult
                {
                    Success = false,
                    Error = $"No release with a {missing} asset was found.",
                    CurrentVersion = current,
                };
            }

            var (release, tag, normalized, asset, shaAsset, kind) = selected.Value;
            string? expectedSha = null;
            if (shaAsset?.BrowserDownloadUrl is not null)
            {
                try
                {
                    var shaText = await _http.GetStringAsync(shaAsset.BrowserDownloadUrl, ct);
                    expectedSha = ExtractSha256(shaText);
                }
                catch
                {
                    _log.Warn("Update", "Could not download SHA256 sidecar; integrity check will be skipped.");
                }
            }

            var newer = IsNewer(normalized, current);
            var downloadUrl = asset.BrowserDownloadUrl;
            string? error = null;
            if (newer && string.IsNullOrWhiteSpace(downloadUrl))
                error = $"Version {normalized} is available but {asset.Name} was not found on the release.";

            // Continuous-build release bodies only link to CHANGELOG.md; pull the
            // actual changelog section so the user can read what changed inline.
            string? changelog = null;
            if (newer)
                changelog = await TryFetchChangelogAsync(configuredUrl, tag, normalized, ct).ConfigureAwait(false);

            return new UpdateCheckResult
            {
                Success = true,
                CurrentVersion = current,
                LatestVersion = normalized,
                ReleaseNotes = Truncate(release.Body, 800),
                ChangelogNotes = changelog,
                DownloadUrl = downloadUrl,
                AssetName = asset.Name,
                AssetKind = kind,
                RequiresElevation = kind == UpdateAssetKind.SetupInstaller,
                Sha256Url = shaAsset?.BrowserDownloadUrl,
                Sha256Expected = expectedSha,
                UpdateAvailable = newer && !string.IsNullOrWhiteSpace(downloadUrl),
                Error = error,
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
        if (string.IsNullOrWhiteSpace(check.DownloadUrl))
            return (false, check.Error ?? "No download URL for the update asset.");

        var kind = check.AssetKind != UpdateAssetKind.None
            ? check.AssetKind
            : InferAssetKind(check.AssetName, check.DownloadUrl);

        try
        {
            return kind == UpdateAssetKind.SetupInstaller
                ? await DownloadAndLaunchSetupAsync(check, ct).ConfigureAwait(false)
                : await DownloadAndSchedulePortableReplaceAsync(check, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("Update", "Apply failed", ex);
            return (false, $"Update download/install failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private async Task<(bool Ok, string Message)> DownloadAndLaunchSetupAsync(UpdateCheckResult check, CancellationToken ct)
    {
        Directory.CreateDirectory(_store.UpdatesDirectory);
        var dest = Path.Combine(_store.UpdatesDirectory, SetupAssetName);
        _log.Info("Update", $"Downloading Setup installer to updates folder (v{check.LatestVersion}).");

        await using (var fs = File.Create(dest))
        await using (var stream = await _http.GetStreamAsync(check.DownloadUrl!, ct))
            await stream.CopyToAsync(fs, ct);

        if (!await VerifySha256Async(dest, check.Sha256Expected).ConfigureAwait(false))
            return (false, "SHA256 verification failed. Update aborted.");

        _log.Info("Update", "Launching elevated Setup installer (UAC may prompt).");
        try
        {
            var started = Process.Start(new ProcessStartInfo
            {
                FileName = dest,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = _store.UpdatesDirectory,
            });
            if (started is null)
            {
                _log.Error("Update", "Failed to start Setup installer process.");
                return (false, "Failed to start the Setup installer.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Warn("Update", "Setup elevation cancelled by user (UAC).");
            return (false, "Update cancelled — approve the Windows UAC prompt to install the Setup update.");
        }
        catch (Win32Exception ex)
        {
            _log.Error("Update", $"Setup launch failed (Win32 {ex.NativeErrorCode}).", ex);
            return (false, $"Could not launch the Setup installer ({ex.Message}).");
        }

        return (true, "Setup installer started. Approve UAC if prompted; the app will quit so files can be replaced.");
    }

    private async Task<(bool Ok, string Message)> DownloadAndSchedulePortableReplaceAsync(
        UpdateCheckResult check, CancellationToken ct)
    {
        if (IsManagedInstall())
        {
            _log.Warn("Update", "Refusing portable EXE replace under Program Files / service install.");
            return (false,
                "This install uses WinTAKTracker-Setup (Program Files / Windows Service). " +
                "Open Settings → Updates again, or install the newer Setup from GitHub Releases.");
        }

        var currentExe = Environment.ProcessPath
                         ?? Path.Combine(AppContext.BaseDirectory, PortableAssetName);
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return (false, "Could not resolve the running EXE path for update replace.");

        if (!CanWriteBeside(currentExe))
        {
            _log.Warn("Update", "Running EXE directory is not writable; portable replace cannot proceed.");
            return (false,
                "Cannot replace the running EXE (folder not writable). " +
                "Move the portable EXE to a user-writable folder, or install via WinTAKTracker-Setup.");
        }

        Directory.CreateDirectory(_store.UpdatesDirectory);
        var dest = Path.Combine(_store.UpdatesDirectory, PortableAssetName);
        _log.Info("Update", $"Downloading portable EXE to updates folder (v{check.LatestVersion}).");

        await using (var fs = File.Create(dest))
        await using (var stream = await _http.GetStreamAsync(check.DownloadUrl!, ct))
            await stream.CopyToAsync(fs, ct);

        if (!await VerifySha256Async(dest, check.Sha256Expected).ConfigureAwait(false))
            return (false, "SHA256 verification failed. Update aborted.");

        // Helper waits for this PID to exit, then retries copy (EXE lock / AV), relaunches, cleans up.
        // Config/certs under %LocalAppData%\WinTAKTracker\ are untouched.
        var bat = Path.Combine(_store.UpdatesDirectory, "apply-update.cmd");
        var logFile = Path.Combine(_store.UpdatesDirectory, "apply-update.log");
        const string script = """
            @echo off
            setlocal EnableExtensions
            set "PID=%~1"
            set "SRC=%~2"
            set "DST=%~3"
            set "LOG=%~dp0apply-update.log"
            echo [%date% %time%] apply-update start pid=%PID%>>"%LOG%"
            if "%PID%"=="" (echo missing PID>>"%LOG%" & exit /b 1)
            if not exist "%SRC%" (echo missing SRC>>"%LOG%" & exit /b 1)
            if "%DST%"=="" (echo missing DST>>"%LOG%" & exit /b 1)

            set /a WAITED=0
            :wait
            tasklist /FI "PID eq %PID%" /NH 2>NUL | find /I ".exe" >NUL
            if errorlevel 1 goto copyloop
            set /a WAITED+=1
            if %WAITED% GEQ 120 (
              echo timed out waiting for pid %PID%>>"%LOG%"
              exit /b 1
            )
            timeout /t 1 /nobreak >NUL
            goto wait

            :copyloop
            set /a TRIES=0
            :retry
            set /a TRIES+=1
            copy /Y "%SRC%" "%DST%" >NUL 2>&1
            if not errorlevel 1 goto launch
            if %TRIES% GEQ 60 (
              echo copy failed after %TRIES% tries>>"%LOG%"
              exit /b 1
            )
            timeout /t 1 /nobreak >NUL
            goto retry

            :launch
            echo copy ok; relaunching>>"%LOG%"
            start "" "%DST%"
            del "%SRC%" >NUL 2>&1
            del "%~f0" >NUL 2>&1
            exit /b 0
            """;
        await File.WriteAllTextAsync(bat, script, ct);

        var pid = Environment.ProcessId;
        _log.Info("Update", $"Portable update downloaded; scheduling restart swap (pid={pid}). Log: {logFile}");
        var started = Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            Arguments = $"{pid} \"{dest}\" \"{currentExe}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = _store.UpdatesDirectory,
        });
        if (started is null)
        {
            _log.Error("Update", "Failed to start portable update helper script.");
            return (false, "Failed to start the update helper script.");
        }

        return (true, "Update ready. The app will restart.");
    }

    private async Task<bool> VerifySha256Async(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return true;

        var actual = await ComputeSha256Async(path).ConfigureAwait(false);
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return true;

        _log.Error("Update", "SHA256 verification failed.");
        try { File.Delete(path); } catch { /* ignore */ }
        return false;
    }

    /// <summary>
    /// True when the app is a Setup/service install under Program Files (or the Windows Service is registered).
    /// Those installs must update via elevated WinTAKTracker-Setup.exe, not in-place EXE copy.
    /// </summary>
    public static bool IsManagedInstall()
    {
        if (ConfigPaths.IsServiceInstalled())
            return true;

        return IsUnderProgramFiles(Environment.ProcessPath)
               || IsUnderProgramFiles(AppContext.BaseDirectory);
    }

    private static bool IsUnderProgramFiles(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var full = Path.GetFullPath(path);
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var prefix = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    private static bool CanWriteBeside(string exePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(dir)) return false;
            var probe = Path.Combine(dir, $".wtt-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Prefer a releases list so we can pick the newest tag that actually ships the needed asset.
    /// Configured <c>.../releases/latest</c> is rewritten to <c>.../releases?per_page=20</c>.
    /// </summary>
    internal static string ResolveReleasesListUrl(string configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
            return "https://api.github.com/repos/CopIXus/WinTAKTracker/releases?per_page=20";

        var trimmed = configuredUrl.TrimEnd('/');
        if (trimmed.EndsWith("/releases/latest", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^"/latest".Length] + "?per_page=20";

        return configuredUrl;
    }

    private static List<GitHubRelease> ParseReleases(JsonElement root)
    {
        var list = new List<GitHubRelease>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
            {
                var r = el.Deserialize<GitHubRelease>();
                if (r is not null) list.Add(r);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var r = root.Deserialize<GitHubRelease>();
            if (r is not null) list.Add(r);
        }

        return list;
    }

    private static (GitHubRelease Release, string Tag, string Normalized, GitHubAsset Asset, GitHubAsset? Sha, UpdateAssetKind Kind)?
        SelectBestRelease(IEnumerable<GitHubRelease> releases, bool preferSetup)
    {
        (GitHubRelease Release, string Tag, string Normalized, GitHubAsset Asset, GitHubAsset? Sha, UpdateAssetKind Kind, Version Ver)? best = null;

        foreach (var release in releases)
        {
            if (release.Draft) continue;

            var tag = release.TagName?.Trim() ?? "";
            if (string.IsNullOrEmpty(tag)) continue;

            var normalized = Normalize(tag);
            if (!Version.TryParse(normalized, out var ver)) continue;

            GitHubAsset? asset;
            UpdateAssetKind kind;
            GitHubAsset? sha;

            if (preferSetup)
            {
                asset = FindNamedAsset(release.Assets, SetupAssetName);
                if (asset?.BrowserDownloadUrl is null) continue;
                kind = UpdateAssetKind.SetupInstaller;
                sha = FindNamedAsset(release.Assets, SetupAssetName + ".sha256")
                      ?? FindShaAsset(release.Assets, preferSetup: true);
            }
            else
            {
                asset = FindNamedAsset(release.Assets, PortableAssetName);
                if (asset?.BrowserDownloadUrl is null) continue;
                kind = UpdateAssetKind.PortableExe;
                sha = FindNamedAsset(release.Assets, PortableAssetName + ".sha256")
                      ?? FindShaAsset(release.Assets, preferSetup: false);
            }

            if (best is null || ver > best.Value.Ver)
                best = (release, tag, normalized, asset, sha, kind, ver);
        }

        if (best is null) return null;
        var b = best.Value;
        return (b.Release, b.Tag, b.Normalized, b.Asset, b.Sha, b.Kind);
    }

    private static UpdateAssetKind InferAssetKind(string? assetName, string? url)
    {
        var name = assetName ?? "";
        if (name.Equals(SetupAssetName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(url) && url.Contains(SetupAssetName, StringComparison.OrdinalIgnoreCase)))
            return UpdateAssetKind.SetupInstaller;
        return UpdateAssetKind.PortableExe;
    }

    private static GitHubAsset? FindNamedAsset(List<GitHubAsset>? assets, string name)
    {
        if (assets is null) return null;
        return assets.FirstOrDefault(a =>
            a.Name is not null &&
            a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static GitHubAsset? FindShaAsset(List<GitHubAsset>? assets, bool preferSetup)
    {
        if (assets is null) return null;
        var preferred = preferSetup ? SetupAssetName + ".sha256" : PortableAssetName + ".sha256";
        return assets.FirstOrDefault(a =>
                   a.Name is not null &&
                   a.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(a =>
                   a.Name is not null &&
                   (a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                    a.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase)));
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

    internal static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(Normalize(latest), out var l)) return false;
        if (!Version.TryParse(Normalize(current), out var c)) return true;
        return l > c;
    }

    /// <summary>
    /// Normalize tags like <c>build-0.1.5</c>, <c>v0.1.5</c>, <c>0.1.5+sha</c>, <c>0.1.5-beta</c> to <c>0.1.5</c>.
    /// </summary>
    internal static string Normalize(string v)
    {
        v = v.Trim();
        if (v.StartsWith("build-", StringComparison.OrdinalIgnoreCase))
            v = v["build-".Length..];
        else if (v.Length > 1 && (v[0] is 'v' or 'V') && char.IsDigit(v[1]))
            v = v[1..];

        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];

        // SemVer pre-release only after a numeric version (0.1.5-beta), never strip "build-…" prefixes again.
        var m = LeadingSemVer.Match(v);
        if (!m.Success)
            return "0.0.0";

        var parts = m.Groups["ver"].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
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

    private async Task<string?> TryFetchChangelogAsync(
        string? configuredApiUrl, string tag, string normalizedVersion, CancellationToken ct)
    {
        var url = TryBuildChangelogUrl(configuredApiUrl, tag);
        if (url is null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var markdown = await _http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            var section = ExtractChangelogSection(markdown, normalizedVersion);
            return section is null ? null : Truncate(MarkdownToPlainText(section), 6000);
        }
        catch
        {
            _log.Warn("Update", "Could not fetch CHANGELOG.md for the new version; showing release body only.");
            return null;
        }
    }

    /// <summary>Raw CHANGELOG.md URL at the release tag, derived from the GitHub releases API URL.</summary>
    internal static string? TryBuildChangelogUrl(string? configuredApiUrl, string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var api = string.IsNullOrWhiteSpace(configuredApiUrl)
            ? "https://api.github.com/repos/CopIXus/WinTAKTracker/releases"
            : configuredApiUrl;
        var m = Regex.Match(api, @"api\.github\.com/repos/(?<owner>[^/]+)/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return "https://raw.githubusercontent.com/" +
               $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}/{Uri.EscapeDataString(tag)}/CHANGELOG.md";
    }

    /// <summary>
    /// Keep-a-Changelog section for <paramref name="version"/>; continuous builds have no
    /// versioned heading, so fall back to the [Unreleased] section (their pending changes).
    /// </summary>
    internal static string? ExtractChangelogSection(string markdown, string version)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var headings = new List<(int Line, string Text)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal))
                headings.Add((i, lines[i]));
        }

        if (headings.Count == 0) return null;

        var start = headings.FindIndex(h =>
            h.Text.Contains($"[{version}]", StringComparison.OrdinalIgnoreCase) ||
            h.Text.Contains($" {version} ", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            start = headings.FindIndex(h => h.Text.Contains("unreleased", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;

        // Skip the heading itself — the UI renders its own "What's new in {version}" title.
        var from = headings[start].Line + 1;
        var to = start + 1 < headings.Count ? headings[start + 1].Line : lines.Length;
        var section = string.Join('\n', lines[from..to]).Trim();
        return section.Length == 0 ? null : section;
    }

    /// <summary>Light markdown cleanup for TextBlock display: links → text, no emphasis/backticks.</summary>
    internal static string MarkdownToPlainText(string markdown)
    {
        var text = Regex.Replace(markdown, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = text.Replace("**", "").Replace("`", "");
        text = Regex.Replace(text, @"^###\s+(.+)$", "$1:", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^##\s+", "", RegexOptions.Multiline);
        // Collapse the 3+ blank lines that heading removal can leave behind.
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

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
