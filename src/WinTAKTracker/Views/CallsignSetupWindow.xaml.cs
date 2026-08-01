using System.Windows;
using WinTAKTracker.Services;

namespace WinTAKTracker.Views;

public partial class CallsignSetupWindow : Window
{
    public string Callsign => CallsignBox.Text.Trim();
    public string Team => TeamBox.Text.Trim();
    public string Phone => PhoneBox.Text.Trim();
    public bool Skipped { get; private set; }

    public CallsignSetupWindow(string defaultTeam, string? version = null)
    {
        InitializeComponent();
        var ver = string.IsNullOrWhiteSpace(version)
            ? (typeof(CallsignSetupWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0")
            : version;
        Title = AppVersionDisplay.WindowTitle(ver, "Set callsign");
        TeamBox.ItemsSource = new[]
        {
            "Cyan", "Blue", "Green", "Yellow", "Orange", "Red", "Purple", "Magenta", "Maroon", "Teal", "White",
        };
        TeamBox.Text = string.IsNullOrWhiteSpace(defaultTeam) ? "Cyan" : defaultTeam;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Callsign))
        {
            ErrorText.Text = "Enter a callsign, or choose “Use computer callsign for now”.";
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Skip_OnClick(object sender, RoutedEventArgs e)
    {
        Skipped = true;
        DialogResult = false;
        Close();
    }
}
