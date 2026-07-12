using System.Windows;
using System.Windows.Input;
using Voiceover.Client.Services;
using Wpf.Ui.Controls;

namespace Voiceover.Client.Views;

public partial class RegisterWindow : FluentWindow
{
    private readonly ApiService _api = new(App.ApiBaseUrl);

    public RegisterWindow()
    {
        InitializeComponent();
    }

    private async void RegisterSubmitButton_Click(object sender, RoutedEventArgs e) => await TryRegisterAsync();

    private void BackToLoginButton_Click(object sender, RoutedEventArgs e)
    {
        new LoginWindow().Show();
        Close();
    }

    private async void UsernameOrPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryRegisterAsync();
    }

    private async Task TryRegisterAsync()
    {
        SetLoading(true);
        try
        {
            var error = await AuthFlow.TryAuthenticateAsync(_api, isRegister: true, UsernameBox.Text, PasswordBox.Password);
            if (error is not null)
            {
                ShowError(error);
                return;
            }

            AuthFlow.CompleteLogin(_api, RememberMeBox.IsChecked == true, this);
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
        RegisterSubmitButton.IsEnabled = !loading;
        BackToLoginButton.IsEnabled = !loading;
    }
}
