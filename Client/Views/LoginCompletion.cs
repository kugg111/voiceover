using System.Windows;
using Voiceover.Client.Services;

namespace Voiceover.Client.Views;

// The WPF-specific tail end of a successful login/register - what happens
// after AuthFlow.TryAuthenticateAsync/CompleteTotpLoginAsync (in
// Client.Core, platform-agnostic) returns Success. Kept separate from that
// shared logic since persisting a remembered session and opening a real
// MainWindow are both WPF/Windows concerns - see the Linux client plan.
// Shared between LoginWindow and RegisterWindow, which both finish a
// successful auth the same way.
internal static class LoginCompletion
{
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
