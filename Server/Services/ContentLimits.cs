namespace Voiceover.Server.Services;

// Shared cap for channel/DM message content - previously unbounded (a plain
// `text` column with no validation anywhere in the write paths), so a
// misbehaving client could store an arbitrarily large ciphertext blob per
// row. 8000 chars is generous for E2EE ciphertext overhead (base64 + AES-GCM
// tag/nonce) over any message a real chat client would send.
public static class ContentLimits
{
    public const int MaxMessageLength = 8000;

    // Shared cap for channel/category names - same reasoning as
    // MaxMessageLength above: previously unbounded on Create/Rename for both,
    // so a client could store an arbitrarily long name that breaks sidebar
    // layout. 100 matches Discord's own channel-name convention.
    public const int MaxNameLength = 100;
}
