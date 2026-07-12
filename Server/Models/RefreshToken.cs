namespace Voiceover.Server.Models;

// Only the SHA-256 hash of the actual refresh token is ever stored - same
// reasoning as PasswordHash on User: if this table leaks, the raw tokens
// (which are bearer credentials, same as a password) shouldn't be
// recoverable from it. The raw value is only ever seen by the client that
// received it in an AuthResponse.
public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    // Null = still active. Set on rotation (a refresh exchanges one token
    // for another) or explicit logout - see AuthController.
    public DateTime? RevokedAt { get; set; }
}
