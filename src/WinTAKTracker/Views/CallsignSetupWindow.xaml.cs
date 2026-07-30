using System.Windows;

namespace WinTAKTracker.Views;

public partial class CallsignSetupWindow : Window
{
    public string Callsign => CallsignBox.Text.Trim();
    public string Team => TeamBox.Text.Trim();
    public bool Skipped { get; private set; }

    public CallsignSetupWindow(string defaultTeam)
    {
        InitializeComponent();
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
            MessageBox.Show(this, "Enter a callsign, or choose “Use computer callsign for now”.",
                "WinTAKTracker", MessageBoxButton.OK, MessageBoxImage.Information);
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
