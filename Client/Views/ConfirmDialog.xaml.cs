using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

// Themed replacement for MessageBox.Show(..., MessageBoxButton.YesNo, ...) -
// matches the rest of the app's dark Fluent styling (title bar, Mica
// backdrop, styled buttons) instead of a native OS dialog that looks out of
// place next to everything else, same reasoning TextInputDialog already
// replaced the old bare-Window text prompt for.
public partial class ConfirmDialog : FluentWindow
{
    public bool Result { get; private set; }

    public ConfirmDialog(string title, string message, string confirmText = "Confirm", bool destructive = false)
    {
        InitializeComponent();
        Title = title;
        DialogTitleBar.Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        // Red for a destructive action (delete, kick, leave) instead of the
        // default accent blurple - same red already used elsewhere in the
        // app for danger actions (e.g. the Leave Voice button).
        if (destructive)
            ConfirmButton.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0x3F, 0x42));
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
