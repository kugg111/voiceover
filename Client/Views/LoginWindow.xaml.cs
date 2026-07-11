using System.Net.Http;
using System.Windows;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class LoginWindow : FluentWindow
{
    private readonly ApiService _api = new(App.ApiBaseUrl);

    public LoginWindow()
    {
        InitializeComponent();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await TryAuth(isRegister: false);
    }

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        await TryAuth(isRegister: true);
    }

    private async Task TryAuth(bool isRegister)
    {
        SetLoading(true);
        try
        {
            var username = UsernameBox.Text.Trim();
            var password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                ShowError("Please enter a username and password.");
                return;
            }

            bool success = isRegister
                ? await _api.RegisterAsync(username, password)
                : await _api.LoginAsync(username, password);

            if (!success)
            {
                ShowError(isRegister ? "That username is already taken." : "Invalid username or password.");
                return;
            }

            if (RememberMeBox.IsChecked == true)
            {
                // Matches JwtTokenService.CreateToken's known 7-day expiry -
                // approximated here rather than parsed out of the JWT itself,
                // to avoid pulling in a JWT-decoding dependency for one field.
                SessionStorage.Save(_api.Token!, _api.CurrentUserId!.Value, _api.CurrentUsername!, DateTime.UtcNow.AddDays(7), _api.CurrentUserAvatarUrl);
            }
            else
            {
                SessionStorage.Clear();
            }

            var main = new MainWindow(_api);
            main.Show();
            Close();
        }
        catch (HttpRequestException)
        {
            ShowError("Could not reach the server. Is it running?");
        }
        catch (TaskCanceledException)
        {
            ShowError("The request timed out. Is the server running?");
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
}
