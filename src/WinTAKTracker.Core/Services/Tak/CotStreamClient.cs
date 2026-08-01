using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
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
    private static readonly TimeSpan IdenticalErrorLogInterval = TimeSpan.FromMinutes(2);

    private readonly AppConfigStore _store;
    private readonly IRedactedLogger _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private X509Certificate2Collection? _clientCerts;
    private int _backoffSeconds = 2;
    private int _consecutiveFailures;
    private string? _lastLoggedFailureKey;
    private DateTimeOffset _lastLoggedFailureUtc = DateTimeOffset.MinValue;
    /// <summary>Set when remote cert callback rejects — distinguishes server-trust vs client-cert faults.</summary>
    private string? _pendingServerTrustReject;

    /// <summary>
    /// infra-TAK TAK Server fail2ban jail: ~20 TLS handshake failures / 5 minutes → UFW ban.
    /// Stop auto-reconnect well under that for TLS/cert faults; network faults allow more tries.
    /// </summary>
    private const int MaxConsecutiveTlsFailures = 5;
    private const int MaxConsecutiveNetworkFailures = 10;

    public CotStreamClient(AppConfigStore store, IRedactedLogger log)
    {
        _store = store;
        _log = log;
    }

    public ServerProfile Profile { get; private set; } = new();
    public TakConnectionState State { get; private set; } = TakConnectionState.Disconnected;
    public string? LastErrorCode { get; private set; }
    public DateTimeOffset? LastSendUtc { get; private set; }

    /// <summary>When false (default), reject TLS if trust-store validation fails. When true, SoftCert soft-accept.</summary>
    public bool AllowInsecureTlsSoftAccept { get; set; }

    /// <summary>True when auto-reconnect stopped to avoid fail2ban / hammering — user can retry Connect.</summary>
    public bool AutoReconnectSuspended { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>Clear circuit-breaker so the next Connect/Test may retry (e.g. user toggled Connect).</summary>
    public void ClearAutoReconnectSuspend()
    {
        AutoReconnectSuspended = false;
        _consecutiveFailures = 0;
        _backoffSeconds = 2;
    }

    /// <summary>Update profile metadata without tearing down a live socket.</summary>
    public void ApplyProfile(ServerProfile profile) => Profile = profile;

    public async Task ConnectAsync(ServerProfile profile, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Profile = profile;
            _pendingServerTrustReject = null;
            SetState(TakConnectionState.Connecting);
            await DisconnectCoreAsync().ConfigureAwait(false);

            if (string.Equals(profile.Protocol, "ssl", StringComparison.OrdinalIgnoreCase))
            {
                var missing = DescribeMissingClientCert(profile);
                if (missing is not null)
                {
                    LastErrorCode = missing;
                    LogConnectionFailure(profile, missing);
                    SetState(TakConnectionState.Error);
                    throw new InvalidOperationException(missing);
                }
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var tcp = new TcpClient();
                // Streaming mTLS can exceed short lab timeouts; keep separate from enroll (8446).
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeout.Token);
                await tcp.ConnectAsync(profile.Host, profile.Port, linked.Token).ConfigureAwait(false);

                Stream stream = tcp.GetStream();
                if (string.Equals(profile.Protocol, "ssl", StringComparison.OrdinalIgnoreCase))
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false, RemoteCertificateValidationCallback);
                    var (certs, loadError) = LoadClientCerts(profile);
                    if (certs.Count == 0)
                    {
                        DisposeClientCerts(certs);
                        LastErrorCode = loadError ?? "No client certificate — enroll first";
                        throw new InvalidOperationException(LastErrorCode);
                    }

                    _clientCerts = certs;
                    // Schannel needs a persisted key container (Machine/UserKeySet) — not EphemeralKeySet.
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = profile.Host,
                        ClientCertificates = certs,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        LocalCertificateSelectionCallback = static (_, _, localCertificates, _, _) =>
                        {
                            foreach (var c in localCertificates)
                            {
                                if (c is X509Certificate2 c2 && c2.HasPrivateKey)
                                    return c2;
                            }

                            return localCertificates.Count > 0 ? localCertificates[0] : null!;
                        },
                    }, linked.Token).ConfigureAwait(false);
                    stream = ssl;
                }

                lock (_gate)
                {
                    _tcp = tcp;
                    _stream = stream;
                }

                _backoffSeconds = 2;
                _consecutiveFailures = 0;
                AutoReconnectSuspended = false;
                LastErrorCode = null;
                SetState(TakConnectionState.Connected);
                _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
                _log.Info("TAK", $"Connected profile={ProfileLabel(profile)} via {profile.Protocol}.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LastErrorCode = "Connection timed out (server unreachable or TLS handshake stalled)";
                NoteFailure(profile, LastErrorCode, tlsOrCert: true);
                SetState(TakConnectionState.Error);
                await DisconnectCoreAsync().ConfigureAwait(false);
                throw new TimeoutException(LastErrorCode);
            }
            catch (Exception ex)
            {
                var human = !string.IsNullOrWhiteSpace(_pendingServerTrustReject)
                    ? _pendingServerTrustReject!
                    : HumanizeConnectError(ex);
                NoteFailure(profile, human, IsTlsOrCertFailure(ex, human));
                LastErrorCode = human;
                SetState(TakConnectionState.Error);
                await DisconnectCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<(bool Ok, string Message)> TestAsync(ServerProfile profile, CancellationToken ct = default)
    {
        if (string.Equals(profile.Protocol, "ssl", StringComparison.OrdinalIgnoreCase))
        {
            var missing = DescribeMissingClientCert(profile);
            if (missing is not null)
                return (false, missing);
        }

        try
        {
            await ConnectAsync(profile, ct).ConfigureAwait(false);
            await DisconnectAsync().ConfigureAwait(false);
            return (true, "Connection test passed.");
        }
        catch (Exception ex)
        {
            var msg = LastErrorCode ?? HumanizeConnectError(ex);
            return (false, msg.StartsWith("Connection test", StringComparison.Ordinal)
                ? msg
                : $"Connection test failed: {msg}");
        }
    }

    private string? DescribeMissingClientCert(ServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ClientCertFileName))
            return "No client certificate — enroll first (paste a Portal enroll URL) or import SoftCert/.p12.";

        var path = Path.Combine(_store.CertsDirectory, profile.ClientCertFileName);
        if (!File.Exists(path))
            return "Client certificate file missing — re-enroll or re-import SoftCert/.p12.";

        return null;
    }

    private static string HumanizeConnectError(Exception ex) => ex switch
    {
        InvalidOperationException ioe => ioe.Message,
        TimeoutException te => te.Message,
        OperationCanceledException => "Connection timed out or was canceled",
        AuthenticationException ae when SchannelCertificateLoader.IsSchannelNoCredentials(ae) =>
            "TLS failed — Windows could not use the client certificate private key (Schannel). " +
            "This is a local key-storage bug, not fail2ban. Update WinTAKTracker / re-test; " +
            "re-enroll only if it still fails.",
        AuthenticationException =>
            DescribeAuthenticationFailure(ex),
        SocketException se => $"Network error ({se.SocketErrorCode})",
        IOException => "Stream closed during connect (often TLS handshake rejected)",
        CryptographicException =>
            "Client certificate could not be loaded (bad password or key store). Fix the .p12 before retrying.",
        _ => ex.GetType().Name,
    };

    private static string DescribeAuthenticationFailure(Exception ex)
    {
        var detail = ex.InnerException?.Message;
        if (!string.IsNullOrWhiteSpace(detail) &&
            !detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return "TLS authentication failed — " + detail.Trim() +
                   " Re-enroll or enable Diagnostics soft-accept if the server uses a private CA. " +
                   "Repeated retries can trigger fail2ban.";
        }

        return "TLS authentication failed — client certificate rejected by the TAK Server, " +
               "or the server certificate was not trusted. Re-enroll or enable Diagnostics soft-accept. " +
               "Repeated retries can trigger fail2ban.";
    }

    private static bool IsTlsOrCertFailure(Exception ex, string? human = null)
    {
        if (ex is AuthenticationException or CryptographicException or TimeoutException)
            return true;
        if (ex is InvalidOperationException &&
            (ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains("enroll", StringComparison.OrdinalIgnoreCase)))
            return true;
        human ??= ex.Message;
        return human.Contains("TLS", StringComparison.OrdinalIgnoreCase)
               || human.Contains("certificate", StringComparison.OrdinalIgnoreCase)
               || human.Contains("handshake", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SendAsync(string cotXml, CancellationToken ct = default)
    {
        Stream? stream;
        lock (_gate) stream = _stream;
        if (stream is null || State != TakConnectionState.Connected)
            throw new InvalidOperationException("Not connected.");

        var bytes = Encoding.UTF8.GetBytes(cotXml);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        LastSendUtc = DateTimeOffset.UtcNow;
    }

    public async Task DisconnectAsync()
    {
        SetState(TakConnectionState.Disconnected);
        await DisconnectCoreAsync().ConfigureAwait(false);
    }

    public async Task ReconnectWithBackoffAsync(ServerProfile profile, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && profile.Enabled)
        {
            if (AutoReconnectSuspended)
            {
                SetState(TakConnectionState.Error);
                return;
            }

            SetState(TakConnectionState.Reconnecting);
            try
            {
                await ConnectAsync(profile, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                var human = LastErrorCode ?? HumanizeConnectError(ex);
                var tls = IsTlsOrCertFailure(ex, human);
                var max = tls ? MaxConsecutiveTlsFailures : MaxConsecutiveNetworkFailures;

                if (_consecutiveFailures >= max)
                {
                    SuspendAutoReconnect(profile, human, tls);
                    return;
                }

                // TLS/cert faults: longer delays (infra-TAK fail2ban ≈ 20 TLS fails / 5 min).
                var delay = tls
                    ? Math.Min(Math.Max(_backoffSeconds, 15), 120)
                    : Math.Min(_backoffSeconds, 60);
                _backoffSeconds = Math.Min(_backoffSeconds * 2, tls ? 120 : 60);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void NoteFailure(ServerProfile profile, string error, bool tlsOrCert)
    {
        _consecutiveFailures++;
        LogConnectionFailure(profile, error);
        _ = tlsOrCert;
    }

    private void SuspendAutoReconnect(ServerProfile profile, string lastError, bool tlsOrCert)
    {
        AutoReconnectSuspended = true;
        LastErrorCode = tlsOrCert
            ? $"{lastError} — stopped auto-reconnect after {_consecutiveFailures} failures " +
              "to avoid infra-TAK fail2ban (TLS probes). Fix the certificate/enrollment, then toggle Connect or use Test."
            : $"{lastError} — stopped auto-reconnect after {_consecutiveFailures} failures. " +
              "Check network/DNS/firewall (or fail2ban ban), then toggle Connect or use Test.";
        LogConnectionFailure(profile, LastErrorCode);
        _log.Error("TAK",
            $"Auto-reconnect suspended for '{ProfileLabel(profile)}' after {_consecutiveFailures} failures " +
            $"(tlsOrCert={tlsOrCert}). Manual retry required.");
        SetState(TakConnectionState.Error);
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
                var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n == 0) break;
                // v1: ignore inbound SA
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastErrorCode = $"Stream ended ({ex.GetType().Name})";
            LogConnectionFailure(Profile, LastErrorCode);
        }

        if (State == TakConnectionState.Connected)
            SetState(TakConnectionState.Disconnected);
    }

    private (X509Certificate2Collection Certs, string? Error) LoadClientCerts(ServerProfile profile)
    {
        var col = new X509Certificate2Collection();
        if (string.IsNullOrWhiteSpace(profile.ClientCertFileName))
            return (col, "No client certificate — enroll first");

        var path = Path.Combine(_store.CertsDirectory, profile.ClientCertFileName);
        if (!File.Exists(path))
            return (col, "Client certificate file missing — re-enroll or re-import SoftCert/.p12.");

        var pwd = profile.CertPasswordBlobName is null
            ? ""
            : _store.ReadSecret(profile.CertPasswordBlobName) ?? "";

        try
        {
            col.Add(LoadPfx(path, pwd));
            return (col, null);
        }
        catch (Exception ex)
        {
            var msg = $"Client certificate could not be loaded ({ex.GetType().Name})";
            _log.Error("TAK", $"profile={ProfileLabel(profile)} {msg}");
            return (col, msg);
        }
    }

    private X509Certificate2 LoadPfx(string path, string password) =>
        // Must use MachineKeySet (service) / UserKeySet (portable) — EphemeralKeySet breaks Schannel mTLS on Windows.
        SchannelCertificateLoader.LoadPfx(path, password, _store.DpapiScope);

    private bool RemoteCertificateValidationCallback(
        object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        if (sslPolicyErrors == SslPolicyErrors.None) return true;

        // Accept when configured trust store validates the chain (full CA set from enroll).
        if (!string.IsNullOrWhiteSpace(Profile.TrustStoreFileName)
            && certificate is not null
            && chain is not null
            && TryValidateWithTrustStore(certificate, chain))
        {
            return true;
        }

        if (!AllowInsecureTlsSoftAccept)
        {
            _pendingServerTrustReject =
                "TLS failed — TAK Server certificate not trusted (private CA / incomplete trust store). " +
                "Re-enroll so the full CA chain is saved, or enable Diagnostics → TLS soft-accept for lab CAs. " +
                $"({sslPolicyErrors})";
            if (ShouldLog($"{Profile.Id}|tls-reject|{sslPolicyErrors}"))
                _log.Warn("TAK",
                    $"TLS rejected ({sslPolicyErrors}) profile={ProfileLabel(Profile)} — soft-accept disabled.");
            return false;
        }

        // SoftCert / private CAs often fail default chain building — allow when opted in (rate-limited).
        if (ShouldLog($"{Profile.Id}|soft-accept|{sslPolicyErrors}"))
            _log.Warn("TAK", $"TLS soft-accept ({sslPolicyErrors}) profile={ProfileLabel(Profile)}.");
        return true;
    }

    private bool TryValidateWithTrustStore(X509Certificate certificate, X509Chain chain)
    {
        var trustPath = Path.Combine(_store.CertsDirectory, Profile.TrustStoreFileName!);
        if (!File.Exists(trustPath)) return false;

        X509Certificate2? leaf = null;
        var owned = new List<X509Certificate2>();
        try
        {
            var pwd = Profile.TrustPasswordBlobName is null
                ? (Profile.CertPasswordBlobName is null
                    ? "atakatak"
                    : _store.ReadSecret(Profile.CertPasswordBlobName) ?? "atakatak")
                : _store.ReadSecret(Profile.TrustPasswordBlobName) ?? "";

            var trustCol = new X509Certificate2Collection();
            trustCol.Import(trustPath, pwd, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            foreach (var c in trustCol)
                owned.Add(c);

            // Sidecar PEM from enroll — covers older profiles that only saved ca0 in the .p12.
            var trustFile = Profile.TrustStoreFileName!;
            if (trustFile.EndsWith("-trust.p12", StringComparison.OrdinalIgnoreCase))
            {
                var idPrefix = trustFile[..^"-trust.p12".Length];
                var pemSidecar = Path.Combine(_store.CertsDirectory, $"{idPrefix}-trust-chain.pem");
                if (File.Exists(pemSidecar))
                {
                    foreach (var pemCert in ReadPemCertificates(File.ReadAllText(pemSidecar)))
                    {
                        owned.Add(pemCert);
                        trustCol.Add(pemCert);
                    }
                }
            }

            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            foreach (var ca in trustCol)
                chain.ChainPolicy.CustomTrustStore.Add(ca);

            leaf = new X509Certificate2(certificate);
            return chain.Build(leaf);
        }
        catch (Exception ex)
        {
            if (ShouldLog($"{Profile.Id}|trust-store|{ex.GetType().Name}"))
                _log.Warn("TAK", $"Trust store validation failed ({ex.GetType().Name}) profile={ProfileLabel(Profile)}.");
            return false;
        }
        finally
        {
            leaf?.Dispose();
            foreach (var c in owned) c.Dispose();
        }
    }

    private static List<X509Certificate2> ReadPemCertificates(string pemText)
    {
        var list = new List<X509Certificate2>();
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var idx = 0;
        while (idx < pemText.Length)
        {
            var start = pemText.IndexOf(begin, idx, StringComparison.Ordinal);
            if (start < 0) break;
            var stop = pemText.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0) break;
            stop += end.Length;
            var block = pemText[start..stop];
            try
            {
                list.Add(X509Certificate2.CreateFromPem(block));
            }
            catch
            {
                // skip malformed block
            }

            idx = stop;
        }

        return list;
    }

    private void LogConnectionFailure(ServerProfile profile, string error)
    {
        if (!ShouldLog($"{profile.Id}|{error}"))
            return;
        // Error level so Diagnostics (default min=Error) surfaces connection problems.
        _log.Error("TAK", $"Server '{ProfileLabel(profile)}' id={ShortId(profile.Id)} connection failed: {error}");
    }

    private bool ShouldLog(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(key, _lastLoggedFailureKey, StringComparison.Ordinal)
            && now - _lastLoggedFailureUtc < IdenticalErrorLogInterval)
        {
            return false;
        }

        _lastLoggedFailureKey = key;
        _lastLoggedFailureUtc = now;
        return true;
    }

    private static string ProfileLabel(ServerProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
            return profile.DisplayName.Trim();
        return ShortId(profile.Id);
    }

    private static string ShortId(string id) =>
        string.IsNullOrEmpty(id) ? "?" : id.Length <= 8 ? id : id[..8];

    private async Task DisconnectCoreAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_readLoop is not null)
        {
            try { await Task.WhenAny(_readLoop, Task.Delay(500)).ConfigureAwait(false); } catch { /* ignore */ }
        }

        lock (_gate)
        {
            try { _stream?.Dispose(); } catch { /* ignore */ }
            try { _tcp?.Dispose(); } catch { /* ignore */ }
            _stream = null;
            _tcp = null;
        }

        DisposeClientCerts(_clientCerts);
        _clientCerts = null;

        _cts?.Dispose();
        _cts = null;
        _readLoop = null;
    }

    private static void DisposeClientCerts(X509Certificate2Collection? certs)
    {
        if (certs is null) return;
        foreach (var c in certs)
        {
            try { c.Dispose(); } catch { /* ignore */ }
        }
    }

    private void SetState(TakConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        DisconnectCoreAsync().GetAwaiter().GetResult();
        _connectLock.Dispose();
    }
}
