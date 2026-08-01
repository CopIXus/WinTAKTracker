using System.Net.NetworkInformation;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Tak;

public sealed class ServerConnectionStatus
{
    public required string ProfileId { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; }
    public string Protocol { get; init; } = "ssl";
    public TakConnectionState State { get; init; }
    public string? LastErrorCode { get; init; }
    public DateTimeOffset? LastSendUtc { get; init; }
}

public interface ITakConnectionManager
{
    event EventHandler? StatusChanged;
    /// <summary>Raised when a server stream reaches <see cref="TakConnectionState.Connected"/>.</summary>
    event EventHandler<ServerProfile>? ServerConnected;
    IReadOnlyList<ServerConnectionStatus> GetStatuses();
    bool AnyConnected { get; }
    bool AnyReconnecting { get; }
    Task StartAsync(AppConfig config);
    Task ReloadAsync(AppConfig config);
    Task SendToAllAsync(string cotXml, CancellationToken ct = default);
    Task<(bool Ok, string Message)> TestServerAsync(string profileId, AppConfig config);
    void WipeProfile(AppConfig config, string profileId, AppConfigStore store);
    void WipeAll(AppConfig config, AppConfigStore store);
    Task StopAsync();
}

/// <summary>Multi-server CoT fan-out with reconnect and network-change hooks.</summary>
public sealed class TakConnectionManager : ITakConnectionManager, IDisposable
{
    private readonly AppConfigStore _store;
    private readonly IRedactedLogger _log;
    private readonly Dictionary<string, CotStreamClient> _clients = new();
    private readonly Dictionary<string, CancellationTokenSource> _reconnectCts = new();
    private readonly Dictionary<string, SemaphoreSlim> _connectGates = new();
    private readonly object _gate = new();
    private AppConfig _config = new();
    private CancellationTokenSource? _networkReloadCts;
    private int _networkReloadVersion;

    public TakConnectionManager(AppConfigStore store, IRedactedLogger log)
    {
        _store = store;
        _log = log;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event EventHandler? StatusChanged;
    public event EventHandler<ServerProfile>? ServerConnected;

    public bool AnyConnected => GetStatuses().Any(s => s.State == TakConnectionState.Connected);
    public bool AnyReconnecting => GetStatuses().Any(s => s.State == TakConnectionState.Reconnecting);

    public IReadOnlyList<ServerConnectionStatus> GetStatuses()
    {
        lock (_gate)
        {
            return _config.Servers.Select(p =>
            {
                if (_clients.TryGetValue(p.Id, out var c))
                {
                    return new ServerConnectionStatus
                    {
                        ProfileId = p.Id,
                        DisplayName = p.DisplayName,
                        Enabled = p.Enabled,
                        Protocol = p.Protocol,
                        State = c.State,
                        LastErrorCode = c.LastErrorCode,
                        LastSendUtc = c.LastSendUtc,
                    };
                }

                return new ServerConnectionStatus
                {
                    ProfileId = p.Id,
                    DisplayName = p.DisplayName,
                    Enabled = p.Enabled,
                    Protocol = p.Protocol,
                    State = TakConnectionState.Disconnected,
                };
            }).ToList();
        }
    }

    public async Task StartAsync(AppConfig config)
    {
        _store.EnsureDirectories();
        await ReloadAsync(config).ConfigureAwait(false);
    }

    public async Task ReloadAsync(AppConfig config)
    {
        _store.EnsureDirectories();
        _config = config;
        var enabledIds = config.Servers.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Host))
            .Select(s => s.Id).ToHashSet();

        List<CotStreamClient> toDispose;
        lock (_gate)
        {
            toDispose = _clients.Where(kv => !enabledIds.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            foreach (var id in toDispose.Select(c => c.Profile.Id).ToList())
            {
                CancelReconnect(id);
                _clients.Remove(id);
                if (_connectGates.Remove(id, out var sem))
                    sem.Dispose();
            }
        }

        foreach (var c in toDispose)
        {
            await c.DisconnectAsync().ConfigureAwait(false);
            c.Dispose();
        }

        foreach (var profile in config.Servers.Where(s => enabledIds.Contains(s.Id)))
        {
            CotStreamClient client;
            lock (_gate)
            {
                if (!_clients.TryGetValue(profile.Id, out client!))
                {
                    client = new CotStreamClient(_store, _log);
                    client.StateChanged += (_, _) =>
                    {
                        StatusChanged?.Invoke(this, EventArgs.Empty);
                        if (client.State == TakConnectionState.Connected)
                            ServerConnected?.Invoke(this, client.Profile);
                        // Unexpected stream drop only — failed ConnectAsync owns its own backoff.
                        if (client.State == TakConnectionState.Disconnected)
                            _ = EnsureReconnectAsync(profile.Id);
                    };
                    _clients[profile.Id] = client;
                    _connectGates[profile.Id] = new SemaphoreSlim(1, 1);
                }
            }

            client.AllowInsecureTlsSoftAccept = ResolveSoftAccept(config, profile);

            if (client.State == TakConnectionState.Connected && !ProfileEndpointChanged(client.Profile, profile))
            {
                // Keep healthy sockets across reload/config save; refresh profile metadata only.
                client.ApplyProfile(profile);
                continue;
            }

            if (client.State is TakConnectionState.Connecting or TakConnectionState.Reconnecting
                && !ProfileEndpointChanged(client.Profile, profile))
            {
                // Let the in-flight attempt finish instead of thrashing.
                continue;
            }

            // Fire-and-forget so IPC ReloadConnections stays responsive; connect lock serializes attempts.
            _ = ConnectOrReconnectAsync(profile);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SendToAllAsync(string cotXml, CancellationToken ct = default)
    {
        List<CotStreamClient> clients;
        lock (_gate) clients = _clients.Values.Where(c => c.State == TakConnectionState.Connected).ToList();

        foreach (var client in clients)
        {
            try { await client.SendAsync(cotXml, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log.Warn("TAK", $"Send failed: {ex.GetType().Name}");
            }
        }
    }

    public async Task<(bool Ok, string Message)> TestServerAsync(string profileId, AppConfig config)
    {
        var profile = config.Servers.FirstOrDefault(s => s.Id == profileId);
        if (profile is null) return (false, "Profile not found.");
        if (string.IsNullOrWhiteSpace(profile.Host)) return (false, "Host is empty.");

        using var client = new CotStreamClient(_store, _log)
        {
            AllowInsecureTlsSoftAccept = ResolveSoftAccept(config, profile),
        };
        try
        {
            return await client.TestAsync(profile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var detail = client.LastErrorCode;
            if (!string.IsNullOrWhiteSpace(detail))
                return (false, detail!);
            return (false, $"Connection test failed: {ex.Message}");
        }
    }

    public void WipeProfile(AppConfig config, string profileId, AppConfigStore store)
    {
        var profile = config.Servers.FirstOrDefault(s => s.Id == profileId);
        if (profile is null) return;

        CancelReconnect(profileId);
        lock (_gate)
        {
            if (_clients.Remove(profileId, out var client))
            {
                _ = client.DisconnectAsync();
                client.Dispose();
            }

            if (_connectGates.Remove(profileId, out var sem))
                sem.Dispose();
        }

        DeleteProfileFiles(profile, store);
        config.Servers.RemoveAll(s => s.Id == profileId);
        store.Save(config);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void WipeAll(AppConfig config, AppConfigStore store)
    {
        foreach (var p in config.Servers.ToList())
            WipeProfile(config, p.Id, store);
    }

    public async Task StopAsync()
    {
        List<CotStreamClient> clients;
        lock (_gate)
        {
            foreach (var id in _reconnectCts.Keys.ToList()) CancelReconnect(id);
            clients = _clients.Values.ToList();
            _clients.Clear();
            foreach (var sem in _connectGates.Values) sem.Dispose();
            _connectGates.Clear();
        }

        foreach (var c in clients)
        {
            await c.DisconnectAsync().ConfigureAwait(false);
            c.Dispose();
        }
    }

    private async Task ConnectOrReconnectAsync(ServerProfile profile)
    {
        CotStreamClient? client;
        SemaphoreSlim? connectGate;
        lock (_gate)
        {
            _clients.TryGetValue(profile.Id, out client);
            _connectGates.TryGetValue(profile.Id, out connectGate);
        }

        if (client is null || connectGate is null) return;

        CancelReconnect(profile.Id);

        var startBackoff = false;
        await connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-check after waiting — another reload may have connected us.
            if (client.State == TakConnectionState.Connected && !ProfileEndpointChanged(client.Profile, profile))
                return;

            if (SslMissingClientCert(profile))
            {
                // Avoid endless reconnect when enroll never completed / certs not migrated yet.
                try { await client.ConnectAsync(profile).ConfigureAwait(false); }
                catch { /* LastErrorCode + diagnostics set by client */ }
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var endpointChanged = ProfileEndpointChanged(client.Profile, profile);
            if (client.AutoReconnectSuspended && !endpointChanged)
            {
                // Stay quiet — circuit open to protect against infra-TAK fail2ban (TLS probe bans).
                // User must toggle Connect off/on or change certs/host to retry.
                client.ApplyProfile(profile);
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (endpointChanged || client.AutoReconnectSuspended)
                client.ClearAutoReconnectSuspend();

            try
            {
                await client.ConnectAsync(profile).ConfigureAwait(false);
            }
            catch
            {
                // Do not start a reconnect storm if the circuit already opened (or opens on this fail).
                startBackoff = !client.AutoReconnectSuspended;
            }
        }
        finally
        {
            connectGate.Release();
        }

        // Start backoff only after releasing the gate so it cannot race the first attempt.
        if (startBackoff)
            _ = EnsureReconnectAsync(profile.Id);
    }

    private async Task EnsureReconnectAsync(string profileId)
    {
        var profile = _config.Servers.FirstOrDefault(s => s.Id == profileId);
        if (profile is null || !profile.Enabled) return;
        if (SslMissingClientCert(profile)) return;

        CotStreamClient? client;
        lock (_gate) _clients.TryGetValue(profileId, out client);
        if (client is null) return;
        if (client.AutoReconnectSuspended) return;
        if (client.State is TakConnectionState.Connected or TakConnectionState.Connecting)
            return;

        CancelReconnect(profileId);
        var cts = new CancellationTokenSource();
        lock (_gate) _reconnectCts[profileId] = cts;

        try
        {
            await client.ReconnectWithBackoffAsync(profile, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // canceled by reload / stop
        }
    }

    private bool SslMissingClientCert(ServerProfile profile)
    {
        if (!string.Equals(profile.Protocol, "ssl", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(profile.ClientCertFileName))
            return true;
        return !File.Exists(Path.Combine(_store.CertsDirectory, profile.ClientCertFileName));
    }

    private static bool ProfileEndpointChanged(ServerProfile current, ServerProfile next) =>
        !string.Equals(current.Host, next.Host, StringComparison.OrdinalIgnoreCase)
        || current.Port != next.Port
        || !string.Equals(current.Protocol, next.Protocol, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(current.ClientCertFileName, next.ClientCertFileName, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(current.TrustStoreFileName, next.TrustStoreFileName, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(current.CertPasswordBlobName, next.CertPasswordBlobName, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(current.TrustPasswordBlobName, next.TrustPasswordBlobName, StringComparison.OrdinalIgnoreCase);

    private void CancelReconnect(string profileId)
    {
        lock (_gate)
        {
            if (_reconnectCts.Remove(profileId, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                cts.Dispose();
            }
        }
    }

    private void ScheduleDebouncedReload()
    {
        var version = Interlocked.Increment(ref _networkReloadVersion);
        CancellationTokenSource cts;
        lock (_gate)
        {
            try { _networkReloadCts?.Cancel(); } catch { /* ignore */ }
            _networkReloadCts?.Dispose();
            _networkReloadCts = new CancellationTokenSource();
            cts = _networkReloadCts;
        }

        _ = DebouncedReloadAsync(version, cts.Token);
    }

    private async Task DebouncedReloadAsync(int version, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            if (version != Volatile.Read(ref _networkReloadVersion)) return;
            _log.Info("TAK", "Network change settled — refreshing connections.");
            await ReloadAsync(_config).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // coalesced
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _log.Info("TAK", "Network address changed — scheduling connection refresh.");
        ScheduleDebouncedReload();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _log.Info("TAK", $"Network availability: {e.IsAvailable}");
        if (e.IsAvailable) ScheduleDebouncedReload();
    }

    private static bool ResolveSoftAccept(AppConfig config, ServerProfile profile) =>
        profile.AllowInsecureTlsSoftAccept ?? config.Diagnostics.AllowInsecureTlsSoftAccept;

    private static void DeleteProfileFiles(ServerProfile profile, AppConfigStore store)
    {
        void Del(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var path = Path.Combine(store.CertsDirectory, name);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        Del(profile.ClientCertFileName);
        Del(profile.TrustStoreFileName);
        if (!string.IsNullOrWhiteSpace(profile.Id))
        {
            var chain = Path.Combine(store.CertsDirectory, $"{profile.Id}-trust-chain.pem");
            try { if (File.Exists(chain)) File.Delete(chain); } catch { /* ignore */ }
        }

        if (profile.SecretBlobName is not null) store.DeleteSecret(profile.SecretBlobName);
        if (profile.CertPasswordBlobName is not null) store.DeleteSecret(profile.CertPasswordBlobName);
        if (profile.TrustPasswordBlobName is not null) store.DeleteSecret(profile.TrustPasswordBlobName);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        lock (_gate)
        {
            try { _networkReloadCts?.Cancel(); } catch { /* ignore */ }
            _networkReloadCts?.Dispose();
            _networkReloadCts = null;
        }

        StopAsync().GetAwaiter().GetResult();
    }
}
