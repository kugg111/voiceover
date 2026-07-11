using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Voiceover.Client.Services;

public record SavedSession(string Token, int UserId, string Username, DateTime ExpiresAtUtc, string? AvatarUrl = null);

// Persists a "remember me" session to disk, encrypted with DPAPI so the file
// is unreadable outside this Windows account. Tradeoff worth noting: this is
// a long-lived bearer token (up to 7 days, matching the server's JWT expiry)
// that isn't revocable server-side since auth is stateless JWT - fine for a
// friends app, not bank-grade.
public static class SessionStorage
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Voiceover", "session.dat");

    public static void Save(string token, int userId, string username, DateTime expiresAtUtc, string? avatarUrl = null)
    {
        var json = JsonSerializer.Serialize(new SavedSession(token, userId, username, expiresAtUtc, avatarUrl));
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
            var session = JsonSerializer.Deserialize<SavedSession>(Encoding.UTF8.GetString(plainBytes));

            if (session is null || session.ExpiresAtUtc <= DateTime.UtcNow)
            {
                Clear();
                return null;
            }

            return session;
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

    public static void Clear()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
