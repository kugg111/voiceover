using System.Net.Http;
using System.Windows;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

internal enum AuthOutcome { Success, Failed, TwoFactorRequired }

// ChallengeToken is only set when Outcome == TwoFactorRequired - the caller
// (LoginWindow) carries it forward into CompleteTotpLoginAsync below.
// ErrorMessage is only set when Outcome == Failed.
internal record AuthAttemptResult(AuthOutcome Outcome, string? ErrorMessage, string? ChallengeToken);

// Shared between LoginWindow and RegisterWindow - both forms submit through
// the same validate -> call API -> handle network-error path, and both
// finish a successful auth the same way (save/clear the remembered session,
// launch MainWindow) - kept in one place instead of duplicated across the
// two windows.
internal static class AuthFlow
{
    public static async Task<AuthAttemptResult> TryAuthenticateAsync(ApiService api, bool isRegister, string username, string password)
    {
        username = username.Trim();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new AuthAttemptResult(AuthOutcome.Failed, "Please enter a username and password.", null);

        try
        {
            if (isRegister)
            {
                // Register never returns a 2FA challenge - a brand-new
                // account can't have 2FA enabled yet.
                var registerError = await api.RegisterAsync(username, password);
                if (registerError is not null)
                    return new AuthAttemptResult(AuthOutcome.Failed, registerError, null);

                await UnlockE2eeBestEffortAsync(api, password);
                return new AuthAttemptResult(AuthOutcome.Success, null, null);
            }

            var loginResult = await api.LoginAsync(username, password);
            if (loginResult.Error is not null)
                return new AuthAttemptResult(AuthOutcome.Failed, loginResult.Error, null);

            if (loginResult.RequiresTwoFactor)
                return new AuthAttemptResult(AuthOutcome.TwoFactorRequired, null, loginResult.ChallengeToken);

            await UnlockE2eeBestEffortAsync(api, password);
            return new AuthAttemptResult(AuthOutcome.Success, null, null);
        }
        catch (HttpRequestException)
        {
            return new AuthAttemptResult(AuthOutcome.Failed, "Could not reach the server. Is it running?", null);
        }
        catch (TaskCanceledException)
        {
            return new AuthAttemptResult(AuthOutcome.Failed, "The request timed out. Is the server running?", null);
        }
    }

    // Completes a login that came back with Outcome == TwoFactorRequired -
    // either code or recoveryCode should be set, not both. password is
    // needed again here (not saved from the first attempt) purely to
    // unlock E2EE below, same as a normal login - it's never sent anywhere
    // in this call, LoginWindow just still has it in the password box.
    public static async Task<AuthAttemptResult> CompleteTotpLoginAsync(ApiService api, string challengeToken, string? code, string? recoveryCode, string password)
    {
        try
        {
            var error = await api.CompleteTotpLoginAsync(challengeToken, code, recoveryCode);
            if (error is not null)
                return new AuthAttemptResult(AuthOutcome.Failed, error, null);

            await UnlockE2eeBestEffortAsync(api, password);
            return new AuthAttemptResult(AuthOutcome.Success, null, null);
        }
        catch (HttpRequestException)
        {
            return new AuthAttemptResult(AuthOutcome.Failed, "Could not reach the server. Is it running?", null);
        }
        catch (TaskCanceledException)
        {
            return new AuthAttemptResult(AuthOutcome.Failed, "The request timed out. Is the server running?", null);
        }
    }

    // Best-effort - unlocks (or, for a fresh registration/pre-E2EE account,
    // generates and uploads) this device's E2EE keys now while the password
    // is still in hand. A failure here doesn't fail the login itself
    // (matches this app's existing pattern of not blocking on non-critical
    // follow-up calls, e.g. SetPresenceState) - DMs just stay locked and
    // show a clear placeholder (see E2eeService.DecryptAsync) until the
    // next successful unlock.
    private static async Task UnlockE2eeBestEffortAsync(ApiService api, string password) =>
        await api.E2ee.UnlockAsync(password);

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
