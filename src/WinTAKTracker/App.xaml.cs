using System.Windows;
using WinTAKTracker.Services;
using WinTAKTracker.Services.Host;
using WinTAKTracker.Services.Theme;
using WinTAKTracker.Views;

namespace WinTAKTracker;

public partial class App : Application
{
    private SingleInstanceMutex? _singleInstance;
    private AppHost? _host;
    private ThemeManager? _theme;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _theme = new ThemeManager();
        _theme.Start();

        UiThreadMarshal.InvokeAsync = async action =>
        {
            if (Current?.Dispatcher is null || Current.Dispatcher.CheckAccess())
            {
                await action().ConfigureAwait(false);
                return;
            }

            await Current.Dispatcher.InvokeAsync(action).Task.Unwrap().ConfigureAwait(false);
        };

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
            MaybePromptCallsignSetup(_host);
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

            var detail = ex is UnauthorizedAccessException or IOException
                ? $"{ex.Message}\n\n" +
                  $"Usually this means %ProgramData%\\WinTAKTracker is locked to SYSTEM after Setup.\n" +
                  $"The tray does not need admin. Fix once (elevated PowerShell), then relaunch:\n\n" +
                  $"icacls \"%ProgramData%\\WinTAKTracker\" /grant \"*S-1-5-32-545:(OI)(CI)M\" /T\n\n" +
                  $"Or reinstall WinTAKTracker-Setup.exe (newer builds set this ACL automatically)."
                : ex.Message;

            MessageBox.Show(
                $"WinTAKTracker failed to start:\n\n{detail}",
                "WinTAKTracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static void MaybePromptCallsignSetup(AppHost host)
    {
        if (!host.CurrentUserNeedsCallsignSetup()) return;

        var dlg = new CallsignSetupWindow(host.Config.ComputerIdentity.Team);
        var result = dlg.ShowDialog();
        if (result == true && !string.IsNullOrWhiteSpace(dlg.Callsign))
        {
            host.SaveCurrentUserIdentity(
                dlg.Callsign,
                string.IsNullOrWhiteSpace(dlg.Team) ? "Cyan" : dlg.Team,
                "Team Member",
                host.Config.ComputerIdentity.CotType);
        }
        else
        {
            // Dismiss once — fall back to computer callsign until they set one in Settings.
            host.DismissCurrentUserSetupPrompt();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _theme?.Dispose();
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
