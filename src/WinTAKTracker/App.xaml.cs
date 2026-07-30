using System.Windows;
using WinTAKTracker.Services;

namespace WinTAKTracker;

public partial class App : Application
{
    private SingleInstanceMutex? _singleInstance;
    private AppHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
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

        _host = new AppHost();
        await _host.StartAsync();
        _host.Tray.ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
