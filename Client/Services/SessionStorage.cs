using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voiceover.Client.Services;

// Stores the long-lived refresh token, not an access token - access tokens
// are short-lived now (see JwtTokenService.AccessTokenLifetime) and would
// almost always already be stale by the time a "remember me" session gets
// reloaded days later. ApiService.RestoreSessionAsync exchanges this for a
// fresh access token on startup instead. Unlike the old plain-JWT scheme,
// this refresh token IS revocable server-side (RefreshToken.RevokedAt) - see
// AuthController's /logout and /logout-all.
public record SavedSession(string RefreshToken, int UserId, string Username, string? AvatarUrl = null);

// Persists a "remember me" session to disk, encrypted with DPAPI so the file
// is unreadable outside this Windows account.
public static class SessionStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "session.dat");

    public static void Save(string refreshToken, int userId, string username, string? avatarUrl = null)
    {
        var json = JsonSerializer.Serialize(new SavedSession(refreshToken, userId, username, avatarUrl));
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, protectedBytes);
    }

    public static SavedSession? Load()
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(FilePath);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SavedSession>(Encoding.UTF8.GetString(plainBytes));
        }
        catch
        {
            // Corrupted or undecryptable (e.g. the file was copied to a
            // different Windows account) - treat as "not logged in" rather
            // than crash the app on startup.
            Clear();
            return null;
        }
    }

    // The server rotates the refresh token on every use (ApiService.
    // RefreshAccessTokenAsync) - the saved file has to track that or the
    // next launch would try to redeem an already-revoked token.
    public static void UpdateRefreshToken(string refreshToken)
    {
        var saved = Load();
        if (saved is null) return;
        Save(refreshToken, saved.UserId, saved.Username, saved.AvatarUrl);
    }

    public static void Clear()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
