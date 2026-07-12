using System.Security.Cryptography;
using System.Text;

namespace Voiceover.Client.Services;

public enum E2eeUnlockResult { Ok, WrongPassword, ServerError }

// True end-to-end encryption for DMs - the server never holds a usable key
// (see Server/Services/MessageEncryptionService.cs for the at-rest scheme
// this supplements, and Server/Models/User.cs for the key-material columns
// this fills in). Mirrors that service's AES-256-GCM packing convention
// (nonce || tag || ciphertext, base64) so both use identical framing.
//
// Key agreement: ECDH P-256 (System.Security.Cryptography.ECDiffieHellman) -
// both DM participants independently derive the SAME AES key via
// ECDH(A_priv, B_pub) == ECDH(B_priv, A_pub), so no per-conversation key
// ever needs to be generated, stored, or transmitted - only each user's
// long-term public key needs to be known to the other side.
//
// Multi-device: the private key is generated once, then wrapped (AES-GCM)
// under a key derived from the account password via PBKDF2, and the
// wrapped blob is stored server-side - safe, since it's useless without the
// raw password, which the server never has in a form usable to re-derive
// the wrapping key. See UnlockAsync/UnlockWithCachedKeyAsync.
public class E2eeService
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int Pbkdf2Iterations = 600_000; // current OWASP ballpark for PBKDF2-HMAC-SHA256

    private readonly ApiService _api;
    private ECDiffieHellman? _privateKey;
    private byte[]? _wrappingKey;
    private readonly Dictionary<int, byte[]> _peerPublicKeyCache = new();
    private readonly Dictionary<int, byte[]> _derivedPairKeyCache = new();

    public bool IsUnlocked => _privateKey is not null;
    public string? CurrentWrappingKeyBase64 => _wrappingKey is null ? null : Convert.ToBase64String(_wrappingKey);

    public E2eeService(ApiService api) => _api = api;

    // Called after a successful login/register (interactive - password is
    // in hand). If the account already has key material, unwraps it; if
    // not (fresh registration, or a pre-E2EE account logging in for the
    // first time since this shipped), generates and uploads a brand new
    // keypair instead.
    public async Task<E2eeUnlockResult> UnlockAsync(string password)
    {
        var material = await _api.GetMyKeyMaterialAsync();
        if (material is null)
            return E2eeUnlockResult.ServerError;

        if (material.WrappedPrivateKey is null || material.PrivateKeySalt is null || material.PublicKey is null)
            return await GenerateAndUploadNewKeysAsync(password) ? E2eeUnlockResult.Ok : E2eeUnlockResult.ServerError;

        var salt = Convert.FromBase64String(material.PrivateKeySalt);
        var wrappingKey = DeriveWrappingKey(password, salt);
        return TryUnwrapAndLoad(material.WrappedPrivateKey, material.PublicKey, wrappingKey);
    }

    // Restores an unlock from a wrapping key cached locally (DPAPI-protected
    // - see SessionStorage) rather than re-deriving it from a password, for
    // a "remember me" session restore where no password was entered this
    // launch.
    public async Task<E2eeUnlockResult> UnlockWithCachedKeyAsync(byte[] wrappingKey)
    {
        var material = await _api.GetMyKeyMaterialAsync();
        if (material?.WrappedPrivateKey is null || material.PublicKey is null)
            return E2eeUnlockResult.ServerError;

        return TryUnwrapAndLoad(material.WrappedPrivateKey, material.PublicKey, wrappingKey);
    }

    private E2eeUnlockResult TryUnwrapAndLoad(string wrappedPrivateKeyBase64, string publicKeyBase64, byte[] wrappingKey)
    {
        try
        {
            var privateKeyBytes = UnwrapBytes(Convert.FromBase64String(wrappedPrivateKeyBase64), wrappingKey);
            var ecdh = ECDiffieHellman.Create();
            ecdh.ImportPkcs8PrivateKey(privateKeyBytes, out _);

            _privateKey?.Dispose();
            _privateKey = ecdh;
            _wrappingKey = wrappingKey;
            return E2eeUnlockResult.Ok;
        }
        catch (CryptographicException)
        {
            // Wrong password (or wrong cached key) - the AES-GCM tag check
            // fails cleanly rather than producing garbage plaintext, so this
            // is a reliable "that password is wrong" signal, not a crash.
            return E2eeUnlockResult.WrongPassword;
        }
    }

    private async Task<bool> GenerateAndUploadNewKeysAsync(string password)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyBytes = ecdh.ExportSubjectPublicKeyInfo();
        var privateKeyBytes = ecdh.ExportPkcs8PrivateKey();

        var salt = RandomNumberGenerator.GetBytes(16);
        var wrappingKey = DeriveWrappingKey(password, salt);
        var wrapped = WrapBytes(privateKeyBytes, wrappingKey);

        var uploaded = await _api.SetMyKeyMaterialAsync(
            Convert.ToBase64String(publicKeyBytes),
            Convert.ToBase64String(wrapped),
            Convert.ToBase64String(salt));
        if (!uploaded) return false;

        _privateKey?.Dispose();
        _privateKey = ECDiffieHellman.Create();
        _privateKey.ImportPkcs8PrivateKey(privateKeyBytes, out _);
        _wrappingKey = wrappingKey;
        return true;
    }

    // Encrypts plaintext for a specific DM peer. Returns null if this
    // device's keys aren't unlocked, or the peer hasn't set up E2EE yet
    // (no public key on file) - callers should treat null as "can't send
    // an encrypted DM right now" rather than silently sending plaintext.
    public async Task<string?> EncryptAsync(int otherUserId, string plaintext)
    {
        var key = await GetOrDerivePairKeyAsync(otherUserId);
        if (key is null) return null;

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(packed);
    }

    // Decrypts ciphertext from a specific DM peer. Never throws - a locked
    // keyset, an unknown peer key, or a corrupt/foreign blob all surface as
    // a clear placeholder string instead of crashing the message list.
    public async Task<string> DecryptAsync(int otherUserId, string packedBase64)
    {
        var key = await GetOrDerivePairKeyAsync(otherUserId);
        if (key is null) return "[Encrypted message - your keys aren't unlocked yet]";

        try
        {
            return DecryptPacked(packedBase64, key);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return "[Unable to decrypt message]";
        }
    }

    // Span-based work pulled into its own non-async method - ref structs
    // (Span<T>/ReadOnlySpan<T>) can't be used as locals inside an async
    // method body in C# 12 (the compiler can't prove they never live across
    // an await), so this stays synchronous and DecryptAsync just awaits the
    // key first, then calls straight into this.
    private static string DecryptPacked(string packedBase64, byte[] key)
    {
        var packed = Convert.FromBase64String(packedBase64);
        if (packed.Length < NonceSizeBytes + TagSizeBytes) throw new ArgumentException("Ciphertext too short.");

        var nonce = packed.AsSpan(0, NonceSizeBytes);
        var tag = packed.AsSpan(NonceSizeBytes, TagSizeBytes);
        var ciphertext = packed.AsSpan(NonceSizeBytes + TagSizeBytes);
        var plaintextBytes = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private async Task<byte[]?> GetOrDerivePairKeyAsync(int otherUserId)
    {
        if (_derivedPairKeyCache.TryGetValue(otherUserId, out var cached)) return cached;
        if (_privateKey is null) return null;

        if (!_peerPublicKeyCache.TryGetValue(otherUserId, out var peerPublicKeyBytes))
        {
            var publicKeyBase64 = await _api.GetPublicKeyAsync(otherUserId);
            if (publicKeyBase64 is null) return null;

            peerPublicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            _peerPublicKeyCache[otherUserId] = peerPublicKeyBytes;
        }

        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKeyBytes, out _);

        var sharedSecret = _privateKey.DeriveRawSecretAgreement(peerEcdh.PublicKey);

        // Sorted so both participants build the identical info string
        // regardless of who's "self" and who's "other" for a given call.
        var myUserId = _api.CurrentUserId!.Value;
        var (loId, hiId) = myUserId < otherUserId ? (myUserId, otherUserId) : (otherUserId, myUserId);
        var info = Encoding.UTF8.GetBytes($"voiceover-dm:{loId}:{hiId}");

        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, outputLength: 32, salt: null, info: info);
        _derivedPairKeyCache[otherUserId] = derived;
        return derived;
    }

    private static byte[] DeriveWrappingKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] WrapBytes(byte[] plaintext, byte[] wrappingKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(wrappingKey, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, packed, nonce.Length + tag.Length, ciphertext.Length);
        return packed;
    }

    private static byte[] UnwrapBytes(byte[] packed, byte[] wrappingKey)
    {
        var nonce = packed.AsSpan(0, NonceSizeBytes);
        var tag = packed.AsSpan(NonceSizeBytes, TagSizeBytes);
        var ciphertext = packed.AsSpan(NonceSizeBytes + TagSizeBytes);
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(wrappingKey, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext); // throws CryptographicException on tag mismatch (wrong key)
        return plaintext;
    }
}
