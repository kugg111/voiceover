namespace Voiceover.Server.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // E2EE key material for DMs (see E2eeService client-side) - all null
    // until the user's client next logs in/registers after this shipped.
    // PublicKey is an SPKI-encoded ECDH P-256 public key, safe to hand to
    // anyone. WrappedPrivateKey is the same user's private key, AES-GCM
    // encrypted under a PBKDF2(password) wrapping key the server never
    // has - useless to read without the account password. PrivateKeySalt
    // is the PBKDF2 salt for that derivation (not secret).
    public string? PublicKey { get; set; }
    public string? WrappedPrivateKey { get; set; }
    public string? PrivateKeySalt { get; set; }

    public List<Membership> Memberships { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
}
