using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

// Themed replacement for a bare System.Windows.Window prompt - matches the
// rest of the app's dark Fluent styling (title bar, Mica backdrop, styled
// TextBox/Button) instead of falling back to plain OS chrome.
public partial class TextInputDialog : FluentWindow
{
    public string? Result { get; private set; }

    public TextInputDialog(string title, string label)
    {
        InitializeComponent();
        Title = title;
        DialogTitleBar.Title = title;
        LabelText.Text = label;
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Submit();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Submit();
    }

    private void Submit()
    {
        Result = InputBox.Text;
        Close();
    }
}
