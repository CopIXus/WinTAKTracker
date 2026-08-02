using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Identity;

namespace WinTAKTracker.Services.Ipc;

/// <summary>Named-pipe client used by the tray UI to control the Windows Service.</summary>
public sealed class TrackerIpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TrackerIpcClient(NamedPipeClientStream pipe, StreamReader reader, StreamWriter writer)
    {
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
    }

    public static async Task<TrackerIpcClient?> TryConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var pipe = new NamedPipeClientStream(
                ".", IpcDefaults.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
            var client = new TrackerIpcClient(pipe, reader, writer);
            var ping = await client.CallAsync(IpcMethod.Ping, null, ct).ConfigureAwait(false);
            if (!ping.Ok) { await client.DisposeAsync(); return null; }
            return client;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> IsServiceReachableAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await using var client = await TryConnectAsync(timeout, ct).ConfigureAwait(false);
        return client is not null;
    }

    public Task<IpcResponse> GetStatusAsync(CancellationToken ct = default) =>
        CallAsync(IpcMethod.GetStatus, null, ct);

    public async Task<TrackerStatusDto?> GetStatusDtoAsync(CancellationToken ct = default)
    {
        var response = await GetStatusAsync(ct).ConfigureAwait(false);
        if (!response.Ok || response.Result is null) return null;
        return response.Result.Value.Deserialize<TrackerStatusDto>(IpcJson.Options);
    }

    public Task<IpcResponse> GetConfigAsync(CancellationToken ct = default) =>
        CallAsync(IpcMethod.GetConfig, null, ct);

    public async Task<AppConfig?> GetConfigDtoAsync(CancellationToken ct = default)
    {
        var response = await GetConfigAsync(ct).ConfigureAwait(false);
        if (!response.Ok || response.Result is null) return null;
        return response.Result.Value.Deserialize<AppConfig>(IpcJson.Options);
    }

    public Task<IpcResponse> SetConfigAsync(AppConfig config, CancellationToken ct = default) =>
        CallAsync(IpcMethod.SetConfig, config, ct);

    public Task<IpcResponse> PauseAsync(CancellationToken ct = default) =>
        CallAsync(IpcMethod.Pause, null, ct);

    public Task<IpcResponse> ResumeAsync(CancellationToken ct = default) =>
        CallAsync(IpcMethod.Resume, null, ct);

    public Task<IpcResponse> ReloadConnectionsAsync(CancellationToken ct = default) =>
        CallAsync(IpcMethod.ReloadConnections, null, ct);

    public Task<IpcResponse> SetComputerIdentityAsync(IdentityUpdateDto dto, CancellationToken ct = default) =>
        CallAsync(IpcMethod.SetComputerIdentity, dto, ct);

    public Task<IpcResponse> SetUserIdentityAsync(IdentityUpdateDto dto, CancellationToken ct = default) =>
        CallAsync(IpcMethod.SetUserIdentity, dto, ct);

    public Task<IpcResponse> SetActiveSessionAsync(SessionUpdateDto dto, CancellationToken ct = default) =>
        CallAsync(IpcMethod.SetActiveSession, dto, ct);

    public Task<IpcResponse> DismissUserSetupPromptAsync(IdentityUpdateDto dto, CancellationToken ct = default) =>
        CallAsync(IpcMethod.DismissUserSetupPrompt, dto, ct);

    public Task<IpcResponse> PushGpsFixAsync(GpsFixDto dto, CancellationToken ct = default) =>
        CallAsync(IpcMethod.PushGpsFix, dto, ct);

    public Task<IpcResponse> ClearGpsFixAsync(CancellationToken ct = default) =>
        CallAsync(IpcMethod.ClearGpsFix, null, ct);

    public Task<IpcResponse> UnlockSettingsAsync(string password, CancellationToken ct = default) =>
        CallAsync(IpcMethod.UnlockSettings, new UnlockSettingsDto { Password = password }, ct);

    public Task<IpcResponse> LockSettingsAsync(CancellationToken ct = default) =>
        CallAsync(IpcMethod.LockSettings, null, ct);

    public Task<IpcResponse> SetVideoAnnounceAsync(VideoAnnounceDto dto, CancellationToken ct = default) =>
        CallAsync(IpcMethod.SetVideoAnnounce, dto, ct);

    public async Task NotifyCurrentUserSessionAsync(CancellationToken ct = default)
    {
        await SetActiveSessionAsync(new SessionUpdateDto
        {
            LoggedOn = true,
            UserSid = IdentityResolver.CurrentUserSid(),
            UserName = IdentityResolver.CurrentUserName(),
        }, ct).ConfigureAwait(false);
    }

    private async Task<IpcResponse> CallAsync(IpcMethod method, object? payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = new IpcRequest
            {
                Method = method.ToString(),
                Payload = payload is null
                    ? null
                    : JsonSerializer.SerializeToElement(payload, IpcJson.Options),
            };
            await _writer.WriteLineAsync(IpcJson.Serialize(request).AsMemory(), ct).ConfigureAwait(false);
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return new IpcResponse { Id = request.Id, Ok = false, Error = "Pipe closed." };
            return IpcJson.Deserialize<IpcResponse>(line)
                   ?? new IpcResponse { Id = request.Id, Ok = false, Error = "Invalid response." };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        _reader.Dispose();
        _writer.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
