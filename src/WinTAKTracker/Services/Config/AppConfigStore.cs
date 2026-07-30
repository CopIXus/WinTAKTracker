using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTAKTracker.Services.Config;

/// <summary>
/// Loads/saves config under %LocalAppData%\WinTAKTracker\. Secrets use DPAPI blobs beside config.json.
/// </summary>
public sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _rootDir;
    private readonly string _configPath;
    private readonly string _secretsDir;

    public AppConfigStore(string? rootDir = null)
    {
        _rootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinTAKTracker");
        _configPath = Path.Combine(_rootDir, "config.json");
        _secretsDir = Path.Combine(_rootDir, "secrets");
    }

    public string RootDirectory => _rootDir;
    public string LogsDirectory => Path.Combine(_rootDir, "logs");
    public string CertsDirectory => Path.Combine(_rootDir, "certs");
    public string UpdatesDirectory => Path.Combine(_rootDir, "updates");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_rootDir);
        Directory.CreateDirectory(_secretsDir);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CertsDirectory);
        Directory.CreateDirectory(UpdatesDirectory);
    }

    public AppConfig Load()
    {
        EnsureDirectories();
        if (!File.Exists(_configPath))
        {
            var fresh = new AppConfig();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        EnsureDirectories();
        config.Version = AppConfig.CurrentVersion;
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temp = _configPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _configPath, overwrite: true);
    }

    public void WriteSecret(string blobName, string plaintext)
    {
        EnsureDirectories();
        var safeName = SanitizeFileName(blobName);
        var path = Path.Combine(_secretsDir, safeName + ".dpapi");
        var protectedBytes = DpapiProtector.Protect(System.Text.Encoding.UTF8.GetBytes(plaintext));
        File.WriteAllBytes(path, protectedBytes);
    }

    public string? ReadSecret(string blobName)
    {
        var safeName = SanitizeFileName(blobName);
        var path = Path.Combine(_secretsDir, safeName + ".dpapi");
        if (!File.Exists(path))
            return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = DpapiProtector.Unprotect(protectedBytes);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void DeleteSecret(string blobName)
    {
        var safeName = SanitizeFileName(blobName);
        var path = Path.Combine(_secretsDir, safeName + ".dpapi");
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
