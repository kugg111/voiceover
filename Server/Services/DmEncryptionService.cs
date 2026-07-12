using System.Security.Cryptography;
using System.Text;

namespace Voiceover.Server.Services;

// Encrypts DirectMessage.Content at rest with AES-256-GCM, so a raw
// database dump/backup leak doesn't hand over plaintext DMs. This is
// encryption AT REST, not end-to-end encryption - the server still holds
// the key and decrypts messages to relay/display them (same as almost every
// chat app without E2EE). True E2EE would mean per-user keypairs, client-
// side encrypt/decrypt, and key-exchange/verification UX - a much bigger,
// separate project, not something to bolt on silently here.
//
// Not required at startup (unlike DATABASE_URL) so a local dev session
// without Dm:EncryptionKey configured can still run everything else - only
// actually sending/reading a DM fails, with a clear error, until it's set.
public class DmEncryptionService
{
    private const int NonceSizeBytes = 12; // GCM-standard nonce size
    private const int TagSizeBytes = 16;

    private readonly byte[]? _key;

    public DmEncryptionService(IConfiguration config)
    {
        var base64Key = config["Dm:EncryptionKey"];
        _key = base64Key is null ? null : Convert.FromBase64String(base64Key);
    }

    public string Encrypt(string plaintext)
    {
        if (_key is null)
            throw new InvalidOperationException(
                "Dm:EncryptionKey is not configured. Set it as an env var (Railway) or via " +
                "`dotnet user-secrets set Dm:EncryptionKey \"...\"` for local dev - see DEPLOYMENT.txt.");

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Packed as nonce || tag || ciphertext, base64-encoded - a nonce and
        // tag are needed to decrypt, so they travel with the ciphertext
        // rather than in a separate column.
        var packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(packed);
    }

    // Falls back to returning the input unchanged if it doesn't look like
    // (or doesn't decrypt as) a packed ciphertext - this is what makes
    // turning on encryption for an existing, already-populated DirectMessages
    // table safe: rows written before this feature shipped are plain text,
    // and there's no risky one-time backfill migration needed to keep them
    // readable. Every DM sent from here on is properly encrypted; old ones
    // just keep working as they always did.
    public string Decrypt(string stored)
    {
        if (_key is null)
            throw new InvalidOperationException(
                "Dm:EncryptionKey is not configured. Set it as an env var (Railway) or via " +
                "`dotnet user-secrets set Dm:EncryptionKey \"...\"` for local dev - see DEPLOYMENT.txt.");

        try
        {
            var packed = Convert.FromBase64String(stored);
            if (packed.Length < NonceSizeBytes + TagSizeBytes) return stored;

            var nonce = packed.AsSpan(0, NonceSizeBytes);
            var tag = packed.AsSpan(NonceSizeBytes, TagSizeBytes);
            var ciphertext = packed.AsSpan(NonceSizeBytes + TagSizeBytes);
            var plaintextBytes = new byte[ciphertext.Length];

            using var aesGcm = new AesGcm(_key, TagSizeBytes);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return stored;
        }
    }
}
