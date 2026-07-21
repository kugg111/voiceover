using Avalonia.Controls;
using Voiceover.Client.Services;

namespace Voiceover.Client.Linux;

// The Linux-specific tail end of a successful login/register - what
// happens after AuthFlow.TryAuthenticateAsync/CompleteTotpLoginAsync (in
// Client.Core, platform-agnostic) returns Success. Mirrors the WPF
// client's Client/Views/LoginCompletion.cs, minus session persistence -
// no "remember me" here yet (see SessionStorage.cs in this project).
internal static class LoginCompletion
{
    public static void CompleteLogin(ApiService api, Window currentWindow)
    {
        new MainWindow(api).Show();
        currentWindow.Close();
    }
}
