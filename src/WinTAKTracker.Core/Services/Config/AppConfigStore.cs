using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTAKTracker.Services.Config;

/// <summary>
/// Loads/saves config under a root directory. Secrets use DPAPI blobs beside config.json.
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
    private readonly DataProtectionScope _dpapiScope;

    public AppConfigStore(string? rootDir = null, DataProtectionScope? dpapiScope = null)
    {
        _rootDir = rootDir ?? ConfigPaths.UserRoot;
        _configPath = Path.Combine(_rootDir, "config.json");
        _secretsDir = Path.Combine(_rootDir, "secrets");
        var isMachineRoot = string.Equals(
            Path.GetFullPath(_rootDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(ConfigPaths.MachineRoot).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        _dpapiScope = dpapiScope ?? (isMachineRoot
            ? DataProtectionScope.LocalMachine
            : DataProtectionScope.CurrentUser);
    }

    public static AppConfigStore ForUser() => new(ConfigPaths.UserRoot, DataProtectionScope.CurrentUser);

    public static AppConfigStore ForMachine() => new(ConfigPaths.MachineRoot, DataProtectionScope.LocalMachine);

    public string RootDirectory => _rootDir;
    public DataProtectionScope DpapiScope => _dpapiScope;
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
            fresh.EnsureIdentityDefaults();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            config.EnsureIdentityDefaults();
            return config;
        }
        catch
        {
            var fallback = new AppConfig();
            fallback.EnsureIdentityDefaults();
            return fallback;
        }
    }

    public void Save(AppConfig config)
    {
        EnsureDirectories();
        config.EnsureIdentityDefaults();
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
        var protectedBytes = DpapiProtector.Protect(
            System.Text.Encoding.UTF8.GetBytes(plaintext), _dpapiScope);
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
            var bytes = DpapiProtector.Unprotect(protectedBytes, _dpapiScope);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Retry opposite scope for one-time migration (CU blob under ProgramData, etc.).
            try
            {
                var protectedBytes = File.ReadAllBytes(path);
                var alt = _dpapiScope == DataProtectionScope.LocalMachine
                    ? DataProtectionScope.CurrentUser
                    : DataProtectionScope.LocalMachine;
                var bytes = DpapiProtector.Unprotect(protectedBytes, alt);
                var plaintext = System.Text.Encoding.UTF8.GetString(bytes);
                WriteSecret(blobName, plaintext); // re-protect with preferred scope
                return plaintext;
            }
            catch
            {
                return null;
            }
        }
    }

    public void DeleteSecret(string blobName)
    {
        var safeName = SanitizeFileName(blobName);
        var path = Path.Combine(_secretsDir, safeName + ".dpapi");
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// Copy cleartext config + re-protect secrets from a CurrentUser store into this (machine) store.
    /// Must run while the source user can decrypt CU DPAPI blobs (installer user or tray).
    /// </summary>
    public static void MigrateUserStoreToMachine(AppConfigStore userStore, AppConfigStore machineStore)
    {
        machineStore.EnsureDirectories();
        var config = userStore.Load();
        machineStore.Save(config);
        CompleteUserToMachineMigration(userStore, machineStore);
    }

    /// <summary>
    /// Finish a partial Setup copy (config/certs only): re-protect CU secrets as LocalMachine and
    /// fill any missing cert files. Safe to call repeatedly. Returns true if anything was written.
    /// </summary>
    public static bool CompleteUserToMachineMigration(AppConfigStore userStore, AppConfigStore machineStore)
    {
        if (!string.Equals(
                Path.GetFullPath(machineStore.RootDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(ConfigPaths.MachineRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Directory.Exists(userStore.RootDirectory))
            return false;

        machineStore.EnsureDirectories();
        var changed = false;

        var userSecrets = Path.Combine(userStore.RootDirectory, "secrets");
        if (Directory.Exists(userSecrets))
        {
            foreach (var file in Directory.EnumerateFiles(userSecrets, "*.dpapi"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var machinePath = Path.Combine(machineStore.RootDirectory, "secrets", SanitizeFileName(name) + ".dpapi");
                // Prefer re-protecting from CU when machine blob is missing or still CU-encrypted.
                var plaintext = userStore.ReadSecret(name);
                if (plaintext is null) continue;

                var needsWrite = !File.Exists(machinePath);
                if (!needsWrite)
                {
                    // Probe LM decrypt; if it fails, rewrite from CU plaintext.
                    try
                    {
                        var probe = machineStore.ReadSecret(name);
                        needsWrite = probe is null;
                    }
                    catch
                    {
                        needsWrite = true;
                    }
                }

                if (!needsWrite) continue;
                machineStore.WriteSecret(name, plaintext);
                changed = true;
            }
        }

        var userCerts = userStore.CertsDirectory;
        if (Directory.Exists(userCerts))
        {
            foreach (var file in Directory.EnumerateFiles(userCerts))
            {
                var dest = Path.Combine(machineStore.CertsDirectory, Path.GetFileName(file));
                if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(file).Length)
                    continue;
                File.Copy(file, dest, overwrite: true);
                changed = true;
            }
        }

        return changed;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
