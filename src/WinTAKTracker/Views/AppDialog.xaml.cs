using System.Windows;
using System.Windows.Controls;

namespace WinTAKTracker.Views;

/// <summary>Theme-aware in-app dialog (prefer over system MessageBox for chrome we control).</summary>
public partial class AppDialog : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private MessageBoxResult _cancelResult = MessageBoxResult.Cancel;

    public AppDialog()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (Result == MessageBoxResult.None)
                Result = _cancelResult;
        };
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title = "WinTAKTracker",
        MessageBoxButton buttons = MessageBoxButton.OK,
        bool dangerPrimary = false)
    {
        var dlg = new AppDialog { Title = title };
        if (owner is { IsLoaded: true })
            dlg.Owner = owner;
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = message;
        dlg.BuildButtons(buttons, dangerPrimary);
        dlg.ShowDialog();
        return dlg.Result;
    }

    private void BuildButtons(MessageBoxButton buttons, bool dangerPrimary)
    {
        ButtonRow.Children.Clear();
        _cancelResult = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel,
        };

        void Add(string label, MessageBoxResult result, bool primary, bool danger = false, bool cancel = false)
        {
            var styleKey = danger ? "DangerButton" : primary ? "PrimaryButton" : "SecondaryButton";
            var b = new Button
            {
                Content = label,
                Style = TryFindResource(styleKey) as Style,
                MinWidth = 88,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = primary,
                IsCancel = cancel,
            };
            b.Click += (_, _) =>
            {
                Result = result;
                try { DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes; }
                catch { Close(); }
            };
            ButtonRow.Children.Add(b);
        }

        switch (buttons)
        {
            case MessageBoxButton.OKCancel:
                Add("Cancel", MessageBoxResult.Cancel, primary: false, cancel: true);
                Add("OK", MessageBoxResult.OK, primary: true, danger: dangerPrimary);
                break;
            case MessageBoxButton.YesNo:
                Add("No", MessageBoxResult.No, primary: false, cancel: true);
                Add("Yes", MessageBoxResult.Yes, primary: true, danger: dangerPrimary);
                break;
            case MessageBoxButton.YesNoCancel:
                Add("Cancel", MessageBoxResult.Cancel, primary: false, cancel: true);
                Add("No", MessageBoxResult.No, primary: false);
                Add("Yes", MessageBoxResult.Yes, primary: true, danger: dangerPrimary);
                break;
            default:
                Add("OK", MessageBoxResult.OK, primary: true, cancel: true);
                break;
        }
    }
}
