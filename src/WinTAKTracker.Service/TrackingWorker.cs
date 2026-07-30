using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Host;
using WinTAKTracker.Services.Identity;
using WinTAKTracker.Services.Ipc;

namespace WinTAKTracker.Service;

/// <summary>SCM-hosted worker that runs TrackingHost headless with IPC control.</summary>
public sealed class TrackingWorker : BackgroundService
{
    private readonly ILogger<TrackingWorker> _logger;
    private TrackingHost? _host;
    private TrackerIpcServer? _ipc;
    private SessionIdentityWatcher? _sessions;

    public TrackingWorker(ILogger<TrackingWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Prefer machine store; seed from user store once if ProgramData is empty.
            var machine = AppConfigStore.ForMachine();
            if (!File.Exists(Path.Combine(machine.RootDirectory, "config.json")))
            {
                var user = AppConfigStore.ForUser();
                if (File.Exists(Path.Combine(user.RootDirectory, "config.json")))
                {
                    try
                    {
                        AppConfigStore.MigrateUserStoreToMachine(user, machine);
                        _logger.LogInformation("Migrated user config into {Root}", machine.RootDirectory);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "User→machine config migration failed; starting with fresh machine store.");
                    }
                }
            }

            _host = new TrackingHost(machine, serviceMode: true);
            _ipc = new TrackerIpcServer(_host);
            _ipc.Start();
            _sessions = new SessionIdentityWatcher(_host, _host.Log);

            await _host.StartAsync().ConfigureAwait(false);
            _logger.LogInformation("WinTAKTracker service running. Config root: {Root}", machine.RootDirectory);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // normal shutdown
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinTAKTracker service failed.");
            throw;
        }
        finally
        {
            if (_ipc is not null)
                await _ipc.DisposeAsync().ConfigureAwait(false);
            _sessions?.Dispose();
            _host?.Dispose();
        }
    }
}
