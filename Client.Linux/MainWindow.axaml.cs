using Avalonia.Controls;
using Avalonia.Interactivity;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

public partial class MainWindow : Window
{
    // = null! - only the designer-only parameterless constructor below
    // ever leaves this unset, and that constructor is never used at real
    // runtime (see its own comment).
    private readonly ApiService _api = null!;

    // Parameterless constructor required by Avalonia's XAML loader/designer -
    // never actually used at runtime, App.axaml.cs always goes through
    // LoginWindow -> LoginCompletion first (mirrors the WPF client, whose
    // App.xaml.cs never shows a MainWindow without a real ApiService either).
    public MainWindow() : this(null!) { }

    public MainWindow(ApiService api)
    {
        _api = api;
        InitializeComponent();
        WelcomeText.Text = $"Welcome, {_api?.CurrentUsername}!";
    }

    private void LogOutButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = _api.LogoutAsync();
        SessionStorage.Clear();
        new LoginWindow().Show();
        Close();
    }
}
