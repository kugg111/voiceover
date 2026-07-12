using System.Net.Http;
using System.Windows;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// Shared between LoginWindow and RegisterWindow - both forms submit through
// the same validate -> call API -> handle network-error path, and both
// finish a successful auth the same way (save/clear the remembered session,
// launch MainWindow) - kept in one place instead of duplicated across the
// two windows.
internal static class AuthFlow
{
    // Returns null on success, or an error message to show the user.
    public static async Task<string?> TryAuthenticateAsync(ApiService api, bool isRegister, string username, string password)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return "Please enter a username and password.";

        try
        {
            var success = isRegister
                ? await api.RegisterAsync(username, password)
                : await api.LoginAsync(username, password);

            return success ? null : (isRegister ? "That username is already taken." : "Invalid username or password.");
        }
        catch (HttpRequestException)
        {
            return "Could not reach the server. Is it running?";
        }
        catch (TaskCanceledException)
        {
            return "The request timed out. Is the server running?";
        }
    }

    public static void CompleteLogin(ApiService api, bool rememberMe, Window currentWindow)
    {
        if (rememberMe)
        {
            // Matches JwtTokenService.CreateToken's known 7-day expiry -
            // approximated here rather than parsed out of the JWT itself,
            // to avoid pulling in a JWT-decoding dependency for one field.
            SessionStorage.Save(api.Token!, api.CurrentUserId!.Value, api.CurrentUsername!, DateTime.UtcNow.AddDays(7), api.CurrentUserAvatarUrl);
        }
        else
        {
            SessionStorage.Clear();
        }

        new MainWindow(api).Show();
        currentWindow.Close();
    }
}
