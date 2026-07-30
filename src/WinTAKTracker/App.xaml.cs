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

        try
        {
            _host = new AppHost();
            await _host.StartAsync();
            _host.Tray.ShowSettings();
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinTAKTracker", "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, "startup-crash.log"),
                    $"{DateTimeOffset.Now:o} Startup failed: {ex}\n");
            }
            catch { /* ignore */ }

            MessageBox.Show(
                $"WinTAKTracker failed to start:\n\n{ex.Message}",
                "WinTAKTracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
