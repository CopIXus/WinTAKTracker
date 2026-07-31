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
            // Setup may copy config/certs without re-protecting secrets — CompleteUserToMachineMigration
            // fills gaps when the service can still read CU blobs (rare); tray also completes this as the user.
            // As LocalSystem, open the machine root so interactive users (tray) can Modify it.
            MachineStoreAcl.EnsureUsersCanModify();
            var machine = AppConfigStore.ForMachine();
            var user = AppConfigStore.ForUser();
            if (!File.Exists(Path.Combine(machine.RootDirectory, "config.json")))
            {
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
            else
            {
                try
                {
                    if (AppConfigStore.CompleteUserToMachineMigration(user, machine))
                        _logger.LogInformation("Completed partial user→machine migration (secrets/certs) into {Root}", machine.RootDirectory);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Partial user→machine migration skipped.");
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
