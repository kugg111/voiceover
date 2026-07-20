namespace Voiceover.Server.Models;

// One-time backup codes generated when 2FA is confirmed (see
// TwoFactorService.GenerateRecoveryCodes) - lets a user regain access if
// they lose their authenticator device. Only CodeHash (BCrypt, same as
// PasswordHash) is stored; the raw code is shown to the user exactly once,
// at generation time. Real FK + cascade (like RefreshToken, not the
// deliberately FK-less BannedUser/ModerationLogEntry) since this is
// account-owned security data with no reason to outlive the account.
public class TotpRecoveryCode
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Null = still usable. Set the moment this code is spent - each code
    // works exactly once.
    public DateTime? UsedAt { get; set; }
}
