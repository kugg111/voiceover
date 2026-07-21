using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

public partial class LoginWindow : Window
{
    private readonly ApiService _api = new(App.ApiBaseUrl);
    private string? _pendingChallengeToken;
    private bool _usingRecoveryCode;

    public LoginWindow()
    {
        InitializeComponent();
        // This same ApiService instance becomes MainWindow's long-lived one
        // after a successful login (see LoginCompletion.CompleteLogin), so
        // it needs the "remember me" persistence wiring from the start -
        // mirrors the WPF client's LoginWindow constructor exactly.
        _api.SessionCleared += SessionStorage.Clear;
        _api.RefreshTokenRotated += SessionStorage.UpdateRefreshToken;
    }

    private async void LoginButton_Click(object? sender, RoutedEventArgs e) => await TryLoginAsync();

    private void RegisterButton_Click(object? sender, RoutedEventArgs e)
    {
        new RegisterWindow().Show();
        Close();
    }

    private async void UsernameOrPasswordBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryLoginAsync();
    }

    private async Task TryLoginAsync()
    {
        SetLoading(true);
        try
        {
            var result = await AuthFlow.TryAuthenticateAsync(_api, isRegister: false, UsernameBox.Text ?? "", PasswordBox.Text ?? "");
            switch (result.Outcome)
            {
                case AuthOutcome.Failed:
                    ShowError(result.ErrorMessage!);
                    break;
                case AuthOutcome.TwoFactorRequired:
                    _pendingChallengeToken = result.ChallengeToken;
                    ShowTotpPanel();
                    break;
                case AuthOutcome.Success:
                    LoginCompletion.CompleteLogin(_api, this);
                    break;
            }
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
        LoginButton.IsEnabled = !loading;
        RegisterButton.IsEnabled = !loading;
    }

    // --- 2FA challenge step ---

    private void ShowTotpPanel()
    {
        LoginPanel.IsVisible = false;
        TotpPanel.IsVisible = true;
        TotpCodeBox.Focus();
    }

    private async void TotpVerifyButton_Click(object? sender, RoutedEventArgs e) => await TryCompleteTotpAsync();

    private async void TotpCodeBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryCompleteTotpAsync();
    }

    private async Task TryCompleteTotpAsync()
    {
        if (_pendingChallengeToken is null) return;

        TotpVerifyButton.IsEnabled = false;
        try
        {
            var input = (TotpCodeBox.Text ?? "").Trim();
            var code = _usingRecoveryCode ? null : input;
            var recoveryCode = _usingRecoveryCode ? input : null;

            // Password is still sitting in PasswordBox from the initial
            // submit (never cleared) - needed again here purely to unlock
            // E2EE once the 2FA challenge succeeds, see AuthFlow.
            var result = await AuthFlow.CompleteTotpLoginAsync(_api, _pendingChallengeToken, code, recoveryCode, PasswordBox.Text ?? "");
            if (result.Outcome == AuthOutcome.Failed)
            {
                TotpStatusText.Text = result.ErrorMessage;
                TotpStatusText.IsVisible = true;
                return;
            }

            LoginCompletion.CompleteLogin(_api, this);
        }
        finally
        {
            TotpVerifyButton.IsEnabled = true;
        }
    }

    private void TotpUseRecoveryCodeButton_Click(object? sender, RoutedEventArgs e)
    {
        _usingRecoveryCode = !_usingRecoveryCode;
        TotpLabel.Text = _usingRecoveryCode ? "RECOVERY CODE" : "CODE";
        TotpUseRecoveryCodeButton.Content = _usingRecoveryCode ? "Use authenticator code instead" : "Use a recovery code instead";
        TotpCodeBox.Text = "";
        TotpStatusText.IsVisible = false;
    }

    private void TotpBackButton_Click(object? sender, RoutedEventArgs e)
    {
        _pendingChallengeToken = null;
        _usingRecoveryCode = false;
        TotpCodeBox.Text = "";
        TotpStatusText.IsVisible = false;
        TotpPanel.IsVisible = false;
        LoginPanel.IsVisible = true;
    }
}
