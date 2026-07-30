using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Diagnostics;

namespace WinTAKTracker.Services.Tak;

public enum TakConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error,
}

/// <summary>TLS (ssl) or cleartext TCP CoT stream client for one server.</summary>
public sealed class CotStreamClient : IDisposable
{
    private readonly AppConfigStore _store;
    private readonly IRedactedLogger _log;
    private readonly object _gate = new();
    private TcpClient? _tcp;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private int _backoffSeconds = 2;

    public CotStreamClient(AppConfigStore store, IRedactedLogger log)
    {
        _store = store;
        _log = log;
    }

    public ServerProfile Profile { get; private set; } = new();
    public TakConnectionState State { get; private set; } = TakConnectionState.Disconnected;
    public string? LastErrorCode { get; private set; }
    public DateTimeOffset? LastSendUtc { get; private set; }
    public event EventHandler? StateChanged;

    public async Task ConnectAsync(ServerProfile profile, CancellationToken ct = default)
    {
        Profile = profile;
        SetState(TakConnectionState.Connecting);
        await DisconnectCoreAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeout.Token);
            await tcp.ConnectAsync(profile.Host, profile.Port, linked.Token);

            Stream stream = tcp.GetStream();
            if (string.Equals(profile.Protocol, "ssl", StringComparison.OrdinalIgnoreCase))
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false, RemoteCertificateValidationCallback);
                var certs = LoadClientCerts(profile);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = profile.Host,
                    ClientCertificates = certs,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, linked.Token);
                stream = ssl;
            }

            lock (_gate)
            {
                _tcp = tcp;
                _stream = stream;
            }

            _backoffSeconds = 2;
            SetState(TakConnectionState.Connected);
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
            _log.Info("TAK", $"Connected via {profile.Protocol} (host redacted).");
        }
        catch (Exception ex)
        {
            LastErrorCode = ex.GetType().Name;
            _log.Warn("TAK", $"Connect failed: {ex.GetType().Name}");
            SetState(TakConnectionState.Error);
            await DisconnectCoreAsync();
            throw;
        }
    }

    public async Task<bool> TestAsync(ServerProfile profile, CancellationToken ct = default)
    {
        try
        {
            await ConnectAsync(profile, ct);
            await DisconnectAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task SendAsync(string cotXml, CancellationToken ct = default)
    {
        Stream? stream;
        lock (_gate) stream = _stream;
        if (stream is null || State != TakConnectionState.Connected)
            throw new InvalidOperationException("Not connected.");

        var bytes = Encoding.UTF8.GetBytes(cotXml);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        LastSendUtc = DateTimeOffset.UtcNow;
    }

    public async Task DisconnectAsync()
    {
        SetState(TakConnectionState.Disconnected);
        await DisconnectCoreAsync();
    }

    public async Task ReconnectWithBackoffAsync(ServerProfile profile, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && profile.Enabled)
        {
            SetState(TakConnectionState.Reconnecting);
            try
            {
                await ConnectAsync(profile, ct);
                return;
            }
            catch
            {
                var delay = Math.Min(_backoffSeconds, 60);
                _backoffSeconds = Math.Min(_backoffSeconds * 2, 60);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Stream? stream;
                lock (_gate) stream = _stream;
                if (stream is null) break;
                var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n == 0) break;
                // v1: ignore inbound SA
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastErrorCode = ex.GetType().Name;
            _log.Warn("TAK", $"Stream ended: {ex.GetType().Name}");
        }

        if (State == TakConnectionState.Connected)
            SetState(TakConnectionState.Disconnected);
    }

    private X509Certificate2Collection LoadClientCerts(ServerProfile profile)
    {
        var col = new X509Certificate2Collection();
        if (string.IsNullOrWhiteSpace(profile.ClientCertFileName)) return col;
        var path = Path.Combine(_store.CertsDirectory, profile.ClientCertFileName);
        if (!File.Exists(path)) return col;

        var pwd = profile.CertPasswordBlobName is null
            ? ""
            : _store.ReadSecret(profile.CertPasswordBlobName) ?? "";

        try
        {
            var cert = new X509Certificate2(path, pwd, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            col.Add(cert);
        }
        catch (Exception ex)
        {
            _log.Warn("TAK", $"Client cert load failed: {ex.GetType().Name}");
        }

        return col;
    }

    private bool RemoteCertificateValidationCallback(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None) return true;

        // Accept when we have a configured trust store, or self-signed lab servers.
        if (!string.IsNullOrWhiteSpace(Profile.TrustStoreFileName))
        {
            var trustPath = Path.Combine(_store.CertsDirectory, Profile.TrustStoreFileName);
            if (File.Exists(trustPath) && certificate is not null)
            {
                try
                {
                    var pwd = Profile.TrustPasswordBlobName is null
                        ? (Profile.CertPasswordBlobName is null ? "atakatak" : _store.ReadSecret(Profile.CertPasswordBlobName) ?? "atakatak")
                        : _store.ReadSecret(Profile.TrustPasswordBlobName) ?? "";
                    var trust = new X509Certificate2(trustPath, pwd);
                    if (chain is not null)
                    {
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(trust);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        if (chain.Build(new X509Certificate2(certificate)))
                            return true;
                    }
                }
                catch
                {
                    // fall through to permissive accept for SoftCert-style deployments
                }
            }
        }

        // SoftCert / private CAs often fail default chain building — allow with warning.
        _log.Warn("TAK", $"TLS cert validation soft-accept ({sslPolicyErrors}).");
        return true;
    }

    private async Task DisconnectCoreAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_readLoop is not null)
        {
            try { await Task.WhenAny(_readLoop, Task.Delay(500)); } catch { /* ignore */ }
        }

        lock (_gate)
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _tcp?.Dispose(); } catch { /* ignore */ }
            _stream = null;
            _tcp = null;
        }

        _cts?.Dispose();
        _cts = null;
        _readLoop = null;
    }

    private void SetState(TakConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        DisconnectCoreAsync().GetAwaiter().GetResult();
    }
}
