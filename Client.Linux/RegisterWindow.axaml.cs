using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

public partial class RegisterWindow : Window
{
    private readonly ApiService _api = new(App.ApiBaseUrl);

    public RegisterWindow()
    {
        InitializeComponent();
        // This same ApiService instance becomes MainWindow's long-lived one
        // after a successful registration (see LoginCompletion.CompleteLogin),
        // so it needs the "remember me" persistence wiring from the start -
        // mirrors the WPF client's RegisterWindow constructor exactly.
        _api.SessionCleared += SessionStorage.Clear;
        _api.RefreshTokenRotated += SessionStorage.UpdateRefreshToken;
    }

    private async void RegisterSubmitButton_Click(object? sender, RoutedEventArgs e) => await TryRegisterAsync();

    private void BackToLoginButton_Click(object? sender, RoutedEventArgs e)
    {
        new LoginWindow().Show();
        Close();
    }

    private async void UsernameOrPasswordBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryRegisterAsync();
    }

    private async Task TryRegisterAsync()
    {
        SetLoading(true);
        try
        {
            // Register can only ever come back Success or Failed - a
            // brand-new account can't have 2FA enabled yet, so there's no
            // TwoFactorRequired branch to handle here (unlike LoginWindow).
            var result = await AuthFlow.TryAuthenticateAsync(_api, isRegister: true, UsernameBox.Text ?? "", PasswordBox.Text ?? "");
            if (result.Outcome == AuthOutcome.Failed)
            {
                ShowError(result.ErrorMessage!);
                return;
            }

            LoginCompletion.CompleteLogin(_api, this);
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    private void SetLoading(bool loading)
    {
        RegisterSubmitButton.IsEnabled = !loading;
        BackToLoginButton.IsEnabled = !loading;
    }
}
