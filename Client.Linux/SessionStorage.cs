namespace Voiceover.Client.Linux;

// No persisted "remember me" session on Linux yet, deliberately - the WPF
// client's SessionStorage encrypts the saved refresh token at rest via
// DPAPI (Windows-only), and there's no Linux equivalent (Secret Service/
// libsecret) wired up yet - see the Linux client plan's deferred-items
// list. These exist only so ApiService's SessionCleared/RefreshTokenRotated
// events (see Client.Core/Services/ApiService.cs) have somewhere to
// subscribe to without every call site null-checking - both are no-ops
// for now. Every login is a fresh one until this is implemented for real.
internal static class SessionStorage
{
    public static void Clear() { }
    public static void UpdateRefreshToken(string refreshToken) { }
}
