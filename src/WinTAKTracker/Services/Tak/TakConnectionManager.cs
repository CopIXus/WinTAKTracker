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
    private readonly object _gate = new();
    private AppConfig _config = new();

    public TakConnectionManager(AppConfigStore store, IRedactedLogger log)
    {
        _store = store;
        _log = log;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public event EventHandler? StatusChanged;

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

    public async Task StartAsync(AppConfig config) => await ReloadAsync(config);

    public async Task ReloadAsync(AppConfig config)
    {
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
            }
        }

        foreach (var c in toDispose)
        {
            await c.DisconnectAsync();
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
                        if (client.State is TakConnectionState.Disconnected or TakConnectionState.Error)
                            _ = EnsureReconnectAsync(profile.Id);
                    };
                    _clients[profile.Id] = client;
                }
            }

            if (client.State != TakConnectionState.Connected)
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
            try { await client.SendAsync(cotXml, ct); }
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

        using var client = new CotStreamClient(_store, _log);
        try
        {
            return await client.TestAsync(profile);
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
        }

        foreach (var c in clients)
        {
            await c.DisconnectAsync();
            c.Dispose();
        }
    }

    private async Task ConnectOrReconnectAsync(ServerProfile profile)
    {
        CotStreamClient? client;
        lock (_gate) _clients.TryGetValue(profile.Id, out client);
        if (client is null) return;

        if (SslMissingClientCert(profile))
        {
            // Avoid endless reconnect when enroll never completed.
            try { await client.ConnectAsync(profile); }
            catch { /* LastErrorCode set by client */ }
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            await client.ConnectAsync(profile);
        }
        catch
        {
            await EnsureReconnectAsync(profile.Id);
        }
    }

    private async Task EnsureReconnectAsync(string profileId)
    {
        var profile = _config.Servers.FirstOrDefault(s => s.Id == profileId);
        if (profile is null || !profile.Enabled) return;
        if (SslMissingClientCert(profile)) return;

        CancelReconnect(profileId);
        var cts = new CancellationTokenSource();
        lock (_gate) _reconnectCts[profileId] = cts;

        CotStreamClient? client;
        lock (_gate) _clients.TryGetValue(profileId, out client);
        if (client is null) return;

        await client.ReconnectWithBackoffAsync(profile, cts.Token);
    }

    private bool SslMissingClientCert(ServerProfile profile)
    {
        if (!string.Equals(profile.Protocol, "ssl", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(profile.ClientCertFileName))
            return true;
        return !File.Exists(Path.Combine(_store.CertsDirectory, profile.ClientCertFileName));
    }

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

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _log.Info("TAK", "Network address changed — retrying connections.");
        _ = ReloadAsync(_config);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _log.Info("TAK", $"Network availability: {e.IsAvailable}");
        if (e.IsAvailable) _ = ReloadAsync(_config);
    }

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
        if (profile.SecretBlobName is not null) store.DeleteSecret(profile.SecretBlobName);
        if (profile.CertPasswordBlobName is not null) store.DeleteSecret(profile.CertPasswordBlobName);
        if (profile.TrustPasswordBlobName is not null) store.DeleteSecret(profile.TrustPasswordBlobName);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        StopAsync().GetAwaiter().GetResult();
    }
}
