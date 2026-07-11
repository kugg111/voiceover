using System.Windows;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class CreateOrJoinDialog : FluentWindow
{
    // true = Create selected, false = Join selected, null = closed without choosing.
    public bool? CreateSelected { get; private set; }

    public CreateOrJoinDialog()
    {
        InitializeComponent();
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        CreateSelected = true;
        Close();
    }

    private void JoinButton_Click(object sender, RoutedEventArgs e)
    {
        CreateSelected = false;
        Close();
    }
}
