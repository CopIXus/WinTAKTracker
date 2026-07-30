using System.Windows;

namespace WinTAKTracker.Views;

public partial class PasswordPromptWindow : Window
{
    private readonly bool _requireConfirm;

    public string? Password { get; private set; }

    public PasswordPromptWindow(string title, string prompt, bool requireConfirm = false)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        _requireConfirm = requireConfirm;
        ConfirmPanel.Visibility = requireConfirm ? Visibility.Visible : Visibility.Collapsed;
        Height = requireConfirm ? 260 : 220;
        Loaded += (_, _) => PrimaryBox.Focus();
    }

    public static string? Prompt(Window? owner, string title, string prompt, bool requireConfirm = false)
    {
        var dlg = new PasswordPromptWindow(title, prompt, requireConfirm)
        {
            Owner = owner,
        };
        return dlg.ShowDialog() == true ? dlg.Password : null;
    }

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        var pwd = PrimaryBox.Password ?? "";
        if (string.IsNullOrEmpty(pwd))
        {
            ErrorText.Text = "Enter a password.";
            return;
        }

        if (_requireConfirm)
        {
            if (pwd != (ConfirmBox.Password ?? ""))
            {
                ErrorText.Text = "Passwords do not match.";
                return;
            }
        }

        Password = pwd;
        DialogResult = true;
        Close();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
