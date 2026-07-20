using System.Windows;
using System.Windows.Input;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class LoginWindow : FluentWindow
{
    private readonly ApiService _api = new(App.ApiBaseUrl);
    private string? _pendingChallengeToken;
    private bool _usingRecoveryCode;

    public LoginWindow()
    {
        InitializeComponent();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e) => await TryLoginAsync();

    private void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        new RegisterWindow().Show();
        Close();
    }

    private async void UsernameOrPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryLoginAsync();
    }

    private async Task TryLoginAsync()
    {
        SetLoading(true);
        try
        {
            var result = await AuthFlow.TryAuthenticateAsync(_api, isRegister: false, UsernameBox.Text, PasswordBox.Password);
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
                    AuthFlow.CompleteLogin(_api, RememberMeBox.IsChecked == true, this);
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
        StatusText.Visibility = Visibility.Visible;
    }

    private void SetLoading(bool loading)
    {
        LoginButton.IsEnabled = !loading;
        RegisterButton.IsEnabled = !loading;
    }

    // --- 2FA challenge step ---

    private void ShowTotpPanel()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        TotpPanel.Visibility = Visibility.Visible;
        TotpCodeBox.Focus();
    }

    private async void TotpVerifyButton_Click(object sender, RoutedEventArgs e) => await TryCompleteTotpAsync();

    private async void TotpCodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryCompleteTotpAsync();
    }

    private async Task TryCompleteTotpAsync()
    {
        if (_pendingChallengeToken is null) return;

        TotpVerifyButton.IsEnabled = false;
        try
        {
            var input = TotpCodeBox.Text.Trim();
            var code = _usingRecoveryCode ? null : input;
            var recoveryCode = _usingRecoveryCode ? input : null;

            // Password is still sitting in PasswordBox from the initial
            // submit (never cleared) - needed again here purely to unlock
            // E2EE once the 2FA challenge succeeds, see AuthFlow.
            var result = await AuthFlow.CompleteTotpLoginAsync(_api, _pendingChallengeToken, code, recoveryCode, PasswordBox.Password);
            if (result.Outcome == AuthOutcome.Failed)
            {
                TotpStatusText.Text = result.ErrorMessage;
                TotpStatusText.Visibility = Visibility.Visible;
                return;
            }

            AuthFlow.CompleteLogin(_api, RememberMeBox.IsChecked == true, this);
        }
        finally
        {
            TotpVerifyButton.IsEnabled = true;
        }
    }

    private void TotpUseRecoveryCodeButton_Click(object sender, RoutedEventArgs e)
    {
        _usingRecoveryCode = !_usingRecoveryCode;
        TotpLabel.Text = _usingRecoveryCode ? "RECOVERY CODE" : "CODE";
        TotpUseRecoveryCodeButton.Content = _usingRecoveryCode ? "Use authenticator code instead" : "Use a recovery code instead";
        TotpCodeBox.Text = "";
        TotpStatusText.Visibility = Visibility.Collapsed;
    }

    private void TotpBackButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingChallengeToken = null;
        _usingRecoveryCode = false;
        TotpCodeBox.Text = "";
        TotpStatusText.Visibility = Visibility.Collapsed;
        TotpPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
    }
}
