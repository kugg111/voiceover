namespace Voiceover.Server.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Gates AdminController - checked fresh from the DB on every admin
    // request (never baked into the JWT, which only carries
    // NameIdentifier/Name - see JwtTokenService), so revoking access
    // takes effect immediately rather than waiting for a token to expire.
    public bool IsAdmin { get; set; } = false;

    // Free-text custom status ("brb", "working", etc.) - unlike
    // PresenceState (Online/Away, entirely in-memory - see
    // PresenceService) this is account data the user sets deliberately, so
    // it's persisted and survives reconnects/logins like AvatarUrl does.
    public string? CustomStatus { get; set; }

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

    // TOTP 2FA (see TwoFactorService/AuthController). TotpSecret is
    // base32, plaintext - the server needs the raw value back on every
    // login to compute the current code, so unlike PasswordHash it can't
    // be hashed; same trust boundary as this app's other account secrets
    // (WrappedPrivateKey), no separate KMS/envelope encryption layer.
    // TwoFactorEnabled is kept separate from "TotpSecret is set" so an
    // abandoned/mistyped enrollment (secret generated, confirm code never
    // entered) can't lock anyone out - only Confirm2fa flips this to true.
    public string? TotpSecret { get; set; }
    public bool TwoFactorEnabled { get; set; }

    public List<Membership> Memberships { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
}
