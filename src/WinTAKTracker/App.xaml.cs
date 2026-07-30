using System.Windows;
using WinTAKTracker.Services;
using WinTAKTracker.Services.Config;
using WinTAKTracker.Services.Pause;
using WinTAKTracker.Services.Tray;

namespace WinTAKTracker;

public partial class App : Application
{
    private SingleInstanceMutex? _singleInstance;
    private TrayIconService? _trayIcon;
    private PauseService? _pauseService;
    private AppConfigStore? _configStore;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = SingleInstanceMutex.TryAcquire();
        if (!_singleInstance.IsPrimaryInstance)
        {
            MessageBox.Show(
                "WinTAKTracker is already running. Use the tray icon to open settings.",
                "WinTAKTracker",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _configStore = new AppConfigStore();
        _configStore.EnsureDirectories();
        _ = _configStore.Load();

        _pauseService = new PauseService();
        _trayIcon = new TrayIconService(_pauseService);
        _trayIcon.SetState(TrayIconState.Disconnected);

        // First run: show settings shell so the user finds the app.
        _trayIcon.ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
