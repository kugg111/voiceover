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

            if (!success)
                return isRegister ? "That username is already taken." : "Invalid username or password.";

            // Best-effort - unlocks (or, for a fresh registration/pre-E2EE
            // account, generates and uploads) this device's E2EE keys now
            // while the password is still in hand. A failure here doesn't
            // fail the login itself (matches this app's existing pattern of
            // not blocking on non-critical follow-up calls, e.g.
            // SetPresenceState) - DMs just stay locked and show a clear
            // placeholder (see E2eeService.DecryptAsync) until the next
            // successful unlock.
            await api.E2ee.UnlockAsync(password);

            return null;
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
            SessionStorage.Save(api.RefreshToken!, api.CurrentUserId!.Value, api.CurrentUsername!, api.CurrentUserAvatarUrl, api.E2ee.CurrentWrappingKeyBase64);
        }
        else
        {
            SessionStorage.Clear();
        }

        new MainWindow(api).Show();
        currentWindow.Close();
    }
}
