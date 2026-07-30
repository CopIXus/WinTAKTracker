using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Host;

namespace WinTAKTracker.Services.Ipc;

/// <summary>Named-pipe JSON RPC server for tray ↔ service control.</summary>
public sealed class TrackerIpcServer : IAsyncDisposable
{
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
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
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
                        response = await DispatchAsync(request).ConfigureAwait(false);
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

    private async Task<IpcResponse> DispatchAsync(IpcRequest request)
    {
        var method = request.Method;
        try
        {
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
        _host.ReplaceConfig(config);
        await _host.ReloadConnectionsAsync().ConfigureAwait(false);
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
        _host.SetComputerIdentity(dto.Callsign, dto.Team, dto.Role, dto.CotType);
        return _host.GetStatus();
    }

    private object SetUserIdentity(JsonElement? payload)
    {
        var dto = DeserializePayload<IdentityUpdateDto>(payload);
        if (string.IsNullOrWhiteSpace(dto.UserSid))
            throw new InvalidOperationException("UserSid is required.");
        _host.SetUserIdentity(dto.UserSid!, dto.UserName ?? "", dto.Callsign, dto.Team, dto.Role, dto.CotType);
        return _host.GetStatus();
    }

    private object SetActiveSession(JsonElement? payload)
    {
        var dto = DeserializePayload<SessionUpdateDto>(payload);
        _host.SetActiveSession(dto.LoggedOn ? dto.UserSid : null, dto.UserName);
        return _host.GetStatus();
    }

    private object DismissSetup(JsonElement? payload)
    {
        var dto = DeserializePayload<IdentityUpdateDto>(payload);
        var sid = dto.UserSid ?? throw new InvalidOperationException("UserSid is required.");
        _host.DismissUserSetupPrompt(sid, dto.UserName);
        return _host.GetStatus();
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
