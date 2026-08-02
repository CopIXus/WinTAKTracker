using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Gps;
using WinTAKTracker.Services.Host;

namespace WinTAKTracker.Services.Ipc;

/// <summary>Named-pipe JSON RPC server for tray ↔ service control.</summary>
public sealed class TrackerIpcServer : IAsyncDisposable
{
    private static readonly HashSet<string> MutatingMethods = new(StringComparer.Ordinal)
    {
        nameof(IpcMethod.SetConfig),
        nameof(IpcMethod.Pause),
        nameof(IpcMethod.Resume),
        nameof(IpcMethod.ReloadConnections),
        nameof(IpcMethod.SetComputerIdentity),
        nameof(IpcMethod.SetUserIdentity),
        nameof(IpcMethod.SetActiveSession),
        nameof(IpcMethod.DismissUserSetupPrompt),
        nameof(IpcMethod.PushGpsFix),
        nameof(IpcMethod.ClearGpsFix),
        nameof(IpcMethod.UnlockSettings),
        nameof(IpcMethod.LockSettings),
        nameof(IpcMethod.SetVideoAnnounce),
    };

    /// <summary>
    /// Settings-lock gated: config/identity writes. Companion GPS / session / pause stay available while locked
    /// so tracking continues; UnlockSettings unlocks the service session for Settings Persist.
    /// </summary>
    private static readonly HashSet<string> LockGatedMethods = new(StringComparer.Ordinal)
    {
        nameof(IpcMethod.SetConfig),
        nameof(IpcMethod.SetComputerIdentity),
        nameof(IpcMethod.SetUserIdentity),
        nameof(IpcMethod.DismissUserSetupPrompt),
    };

    private readonly TrackingHost _host;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public TrackerIpcServer(TrackingHost host) => _host = host;

    public void Start()
    {
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _ = HandleClientAsync(pipe, ct);
                pipe = null; // ownership transferred
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _host.Log.Warn("IPC", $"Accept error: {ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            finally
            {
                if (pipe is not null)
                    await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcDefaults.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };

                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    IpcResponse response;
                    try
                    {
                        var request = IpcJson.Deserialize<IpcRequest>(line)
                                      ?? throw new InvalidOperationException("Invalid request.");
                        response = await DispatchAsync(request, pipe).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        response = new IpcResponse { Ok = false, Error = ex.Message };
                    }

                    await writer.WriteLineAsync(IpcJson.Serialize(response)).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _host.Log.Debug("IPC", $"Client ended: {ex.Message}");
            }
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request, NamedPipeServerStream pipe)
    {
        var method = request.Method;
        try
        {
            if (MutatingMethods.Contains(method))
            {
                if (!TryGetInteractiveClientSid(pipe, out var clientSid, out var interactiveError))
                    throw new InvalidOperationException(interactiveError ?? "IPC mutation requires an interactive Windows user.");

                if (LockGatedMethods.Contains(method) && _host.SettingsLock.IsLocked)
                    throw new InvalidOperationException(
                        "Settings are locked — unlock in the tray (or UnlockSettings) before mutating config/identity/GPS.");

                // Multi-session: only the active companion SID may push GPS / claim session when one is set.
                if (method is nameof(IpcMethod.PushGpsFix) or nameof(IpcMethod.SetActiveSession))
                {
                    var sid = ResolveCallerSid(method, request.Payload, clientSid);
                    if (!_host.IsCompanionSidAllowed(sid) &&
                        method == nameof(IpcMethod.PushGpsFix))
                    {
                        throw new InvalidOperationException(
                            "Another interactive companion owns GPS pushes; ignore from this session.");
                    }

                    if (method == nameof(IpcMethod.SetActiveSession) &&
                        !_host.IsCompanionSidAllowed(sid) &&
                        request.Payload is { } p &&
                        p.TryGetProperty("loggedOn", out var logged) &&
                        logged.ValueKind == JsonValueKind.True)
                    {
                        throw new InvalidOperationException(
                            "Another interactive companion is already active; SetActiveSession rejected.");
                    }
                }
            }

            object? result = method switch
            {
                nameof(IpcMethod.Ping) => new { pong = true, utc = DateTimeOffset.UtcNow },
                nameof(IpcMethod.GetStatus) => _host.GetStatus(),
                nameof(IpcMethod.GetConfig) => _host.Config,
                nameof(IpcMethod.SetConfig) => await ApplyConfigAsync(request.Payload),
                nameof(IpcMethod.Pause) => Pause(true),
                nameof(IpcMethod.Resume) => Pause(false),
                nameof(IpcMethod.ReloadConnections) => await ReloadAsync(),
                nameof(IpcMethod.SetComputerIdentity) => SetComputerIdentity(request.Payload),
                nameof(IpcMethod.SetUserIdentity) => SetUserIdentity(request.Payload),
                nameof(IpcMethod.SetActiveSession) => SetActiveSession(request.Payload),
                nameof(IpcMethod.DismissUserSetupPrompt) => DismissSetup(request.Payload),
                nameof(IpcMethod.PushGpsFix) => PushGpsFix(request.Payload),
                nameof(IpcMethod.ClearGpsFix) => ClearGpsFix(),
                nameof(IpcMethod.UnlockSettings) => UnlockSettings(request.Payload),
                nameof(IpcMethod.LockSettings) => LockSettings(),
                nameof(IpcMethod.SetVideoAnnounce) => SetVideoAnnounce(request.Payload),
                _ => throw new InvalidOperationException($"Unknown method '{method}'."),
            };

            return new IpcResponse
            {
                Id = request.Id,
                Ok = true,
                Result = JsonSerializer.SerializeToElement(result, IpcJson.Options),
            };
        }
        catch (Exception ex)
        {
            return new IpcResponse { Id = request.Id, Ok = false, Error = ex.Message };
        }
    }

    private async Task<object> ApplyConfigAsync(JsonElement? payload)
    {
        if (payload is null) throw new InvalidOperationException("Missing config payload.");
        var config = payload.Value.Deserialize<AppConfig>(IpcJson.Options)
                     ?? throw new InvalidOperationException("Invalid config.");
        var before = _host.Config;
        var needsReload = ConfigReconnectComparer.RequiresConnectionReload(before, config);
        _host.ReplaceConfig(config);
        if (needsReload)
            await _host.ReloadConnectionsAsync().ConfigureAwait(false);
        else
            _host.Log.Info("IPC", "SetConfig applied without connection reload (non-connection fields only).");
        return _host.GetStatus();
    }

    private object Pause(bool paused)
    {
        _host.Pause.SetPaused(paused);
        return _host.GetStatus();
    }

    private async Task<object> ReloadAsync()
    {
        await _host.ReloadConnectionsAsync().ConfigureAwait(false);
        return _host.GetStatus();
    }

    private object SetComputerIdentity(JsonElement? payload)
    {
        var dto = DeserializePayload<IdentityUpdateDto>(payload);
        _host.SetComputerIdentity(dto.Callsign, dto.Team, dto.Role, dto.CotType, dto.Phone);
        return _host.GetStatus();
    }

    private object SetUserIdentity(JsonElement? payload)
    {
        var dto = DeserializePayload<IdentityUpdateDto>(payload);
        if (string.IsNullOrWhiteSpace(dto.UserSid))
            throw new InvalidOperationException("UserSid is required.");
        _host.SetUserIdentity(dto.UserSid!, dto.UserName ?? "", dto.Callsign, dto.Team, dto.Role, dto.CotType, dto.Phone);
        return _host.GetStatus();
    }

    private object SetActiveSession(JsonElement? payload)
    {
        var dto = DeserializePayload<SessionUpdateDto>(payload);
        _host.SetActiveSession(dto.LoggedOn ? dto.UserSid : null, dto.UserName);
        return _host.GetStatus();
    }

    private object SetVideoAnnounce(JsonElement? payload)
    {
        var dto = DeserializePayload<VideoAnnounceDto>(payload);
        _host.SetVideoAnnounce(dto.ToState());
        return _host.GetStatus();
    }

    private object DismissSetup(JsonElement? payload)
    {
        var dto = DeserializePayload<IdentityUpdateDto>(payload);
        var sid = dto.UserSid ?? throw new InvalidOperationException("UserSid is required.");
        _host.DismissUserSetupPrompt(sid, dto.UserName);
        return _host.GetStatus();
    }

    private object PushGpsFix(JsonElement? payload)
    {
        var dto = DeserializePayload<GpsFixDto>(payload);
        if (double.IsNaN(dto.Latitude) || double.IsNaN(dto.Longitude))
            throw new InvalidOperationException("Invalid fix coordinates.");

        var source = GpsSourceKind.Companion;
        if (!string.IsNullOrWhiteSpace(dto.Source) &&
            Enum.TryParse<GpsSourceKind>(dto.Source, true, out var parsed) &&
            parsed is not GpsSourceKind.None and not GpsSourceKind.Held)
            source = parsed == GpsSourceKind.WindowsLocation ? GpsSourceKind.Companion : parsed;

        _host.Gps.AcceptExternalFix(new GpsFix
        {
            Latitude = dto.Latitude,
            Longitude = dto.Longitude,
            AltitudeMeters = dto.AltitudeMeters,
            SpeedMetersPerSecond = dto.SpeedMetersPerSecond,
            CourseDegrees = dto.CourseDegrees,
            AccuracyMeters = dto.AccuracyMeters,
            Timestamp = dto.TimestampUtc == default ? DateTimeOffset.UtcNow : dto.TimestampUtc,
            Source = source,
        });
        return _host.GetStatus();
    }

    private object ClearGpsFix()
    {
        _host.Gps.ClearExternalFix();
        return _host.GetStatus();
    }

    private object UnlockSettings(JsonElement? payload)
    {
        var dto = DeserializePayload<UnlockSettingsDto>(payload);
        if (!_host.SettingsLock.TryUnlock(dto.Password ?? ""))
            throw new InvalidOperationException("Incorrect settings lock password.");
        return new { unlocked = true };
    }

    private object LockSettings()
    {
        _host.SettingsLock.Lock();
        return new { locked = _host.SettingsLock.IsLocked };
    }

    private static string? ResolveCallerSid(string method, JsonElement? payload, string? pipeSid)
    {
        if (payload is null) return pipeSid;
        try
        {
            if (method == nameof(IpcMethod.SetActiveSession))
            {
                var dto = payload.Value.Deserialize<SessionUpdateDto>(IpcJson.Options);
                return dto?.UserSid ?? pipeSid;
            }
        }
        catch
        {
            /* ignore */
        }

        return pipeSid;
    }

    /// <summary>
    /// Impersonate the pipe client and require a non-service interactive Windows user.
    /// </summary>
    private static bool TryGetInteractiveClientSid(
        NamedPipeServerStream pipe,
        out string? clientSid,
        out string? error)
    {
        clientSid = null;
        error = null;
        WindowsIdentity? identity = null;
        try
        {
            pipe.RunAsClient(() => { identity = WindowsIdentity.GetCurrent(); });
        }
        catch (Exception ex)
        {
            error = "Could not impersonate pipe client: " + ex.Message;
            return false;
        }

        if (identity is null)
        {
            error = "Pipe client identity unavailable.";
            return false;
        }

        using (identity)
        {
            if (identity.IsSystem || identity.IsAnonymous || identity.IsGuest)
            {
                error = "Non-interactive pipe client rejected.";
                return false;
            }

            var sid = identity.User?.Value ?? "";
            // LocalSystem / LocalService / NetworkService
            if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20")
            {
                error = "Service account pipe clients cannot mutate tracker state.";
                return false;
            }

            // Prefer rejecting tokens that are not interactive logons when detectable.
            if (!IsLikelyInteractive(identity))
            {
                error = "Pipe client is not an interactive Windows user.";
                return false;
            }

            clientSid = sid;
            return true;
        }
    }

    private static bool IsLikelyInteractive(WindowsIdentity identity)
    {
        try
        {
            // Impersonation from a named-pipe client of the tray app is typically Identification or Impersonation.
            if (identity.ImpersonationLevel is TokenImpersonationLevel.None)
                return false;

            // Empty name often means machine/service context.
            if (string.IsNullOrWhiteSpace(identity.Name))
                return false;

            return true;
        }
        catch
        {
            return true; // fail open on exotic tokens after SID checks passed
        }
    }

    private static T DeserializePayload<T>(JsonElement? payload)
    {
        if (payload is null) throw new InvalidOperationException("Missing payload.");
        return payload.Value.Deserialize<T>(IpcJson.Options)
               ?? throw new InvalidOperationException("Invalid payload.");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
