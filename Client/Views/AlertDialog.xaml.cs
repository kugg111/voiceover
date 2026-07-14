using System.Windows;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

// Themed single-button replacement for MessageBox.Show(..., MessageBoxButton.OK, ...) -
// matches the rest of the app's dark Fluent styling instead of a native OS
// dialog, same reasoning ConfirmDialog already replaced the YesNo case for.
public partial class AlertDialog : FluentWindow
{
    public AlertDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        DialogTitleBar.Title = title;
        MessageText.Text = message;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}
