using System.Security.Cryptography;
using System.Text;
using Voiceover.Client.Models;

namespace Voiceover.Client.Services;

public enum E2eeUnlockResult { Ok, WrongPassword, ServerError }

// Content is the single ciphertext body (encrypted once under a fresh
// per-message key); RecipientKeys is that key wrapped individually for every
// current member who could be derived for (see
// E2eeService.EncryptForChannelAsync) - both travel together to
// ChatHub.SendMessage/MessagesController.Edit.
public record ChannelEncryptResult(string Content, List<MessageKeyEnvelope> RecipientKeys);

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

    // Concurrent, not plain Dictionary - this service is called from
    // multiple SignalR hub-event callbacks that can genuinely fire on
    // different background threads at the same time (a message arriving
    // while a key request is being handled, two messages back to back,
    // etc.), and a plain Dictionary isn't safe under concurrent
    // read/write. A torn dictionary read here doesn't just corrupt state -
    // it throws, and since these are called from async void event
    // handlers on non-UI threads, an uncaught exception there crashes the
    // whole process (WPF's DispatcherUnhandledException only catches
    // exceptions raised on the UI thread's Dispatcher, not this).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _peerPublicKeyCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte[]> _derivedPairKeyCache = new();

    // Serializes the ECDH/HKDF derivation itself, not just the caches
    // above - ECDiffieHellman wraps a native crypto handle that isn't
    // documented as safe for concurrent use from multiple threads, and
    // _privateKey is shared across every concurrent call into this class.
    private readonly SemaphoreSlim _cryptoLock = new(1, 1);

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
        catch (Exception ex) when (ex is CryptographicException or FormatException)
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
        return Convert.ToBase64String(WrapBytes(Encoding.UTF8.GetBytes(plaintext), key));
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

    // --- Channel-message keys - a fresh random AES-256 key per MESSAGE,
    // asymmetrically wrapped for every current member of the sending
    // channel's server (see Server/Models/MessageRecipientKey.cs), including
    // the sender itself. Replaces the old one-shared-key-per-server scheme
    // (still visible in git history), which required an already-onboarded
    // member to be online to hand a new member a copy - two brand new
    // members joining together could deadlock forever with neither able to
    // grant the other access. Wrapping fresh per message instead means a
    // member just needs to already be a member when a message is SENT, with
    // no separate grant step ever required - whoever's in the member list
    // at that moment gets a working copy of that message's key, full stop.
    // ---

    // Encrypts plaintext for every current member of a server's channel.
    // Returns null if this device's keys aren't unlocked, or the member list
    // couldn't be fetched, or came back with nobody this device could wrap
    // for at all (never happens in practice - the sender is always in their
    // own server's member list). A peer with no public key on file yet (or a
    // failed derivation) is simply skipped for that one recipient rather
    // than failing the whole send - same "some recipients might not be
    // ready yet" tolerance ChatHub.SendMessage's own member-list clamp
    // already assumes server-side.
    public async Task<ChannelEncryptResult?> EncryptForChannelAsync(int serverId, string plaintext)
    {
        if (_privateKey is null) return null;

        var members = await _api.GetMembersAsync(serverId);
        if (members.Count == 0) return null;

        var messageKey = RandomNumberGenerator.GetBytes(32);
        var content = Convert.ToBase64String(WrapBytes(Encoding.UTF8.GetBytes(plaintext), messageKey));

        var recipientKeys = new List<MessageKeyEnvelope>();
        foreach (var member in members)
        {
            var wrapKey = await DerivePeerKeyAsync(member.UserId, ChannelKeyWrapInfoPrefix);
            if (wrapKey is null) continue;

            recipientKeys.Add(new MessageKeyEnvelope(member.UserId, Convert.ToBase64String(WrapBytes(messageKey, wrapKey))));
        }

        if (recipientKeys.Count == 0) return null;
        return new ChannelEncryptResult(content, recipientKeys);
    }

    // Decrypts a channel message using this device's own wrapped copy of
    // its one-time key (MessageResponse.WrappedKeyForMe) - never throws,
    // same fail-closed placeholder convention as DecryptAsync. A null
    // wrappedKeyForMe means this device was never wrapped for this message
    // at all (joined the server after it was sent, or was missing from the
    // sender's member list at send time) - permanently unreadable by
    // design, not a transient "wait and retry" state like the old
    // shared-key scheme's locked placeholder was.
    public async Task<string> DecryptChannelMessageAsync(int authorId, string? wrappedKeyForMe, string packedContentBase64)
    {
        if (wrappedKeyForMe is null) return "[Encrypted message - no key for this device]";

        var wrapKey = await DerivePeerKeyAsync(authorId, ChannelKeyWrapInfoPrefix);
        if (wrapKey is null) return "[Encrypted message - your keys aren't unlocked yet]";

        try
        {
            var messageKey = UnwrapBytes(Convert.FromBase64String(wrappedKeyForMe), wrapKey);
            return DecryptPacked(packedContentBase64, messageKey);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return "[Unable to decrypt message]";
        }
    }

    // No server id needed (unlike the old per-server wrap key) - each
    // message's key is freshly random and never reused, so the only actual
    // requirement is domain separation from a DM's own message key, not
    // per-server separation.
    private const string ChannelKeyWrapInfoPrefix = "voiceover-channelkey-wrap";

    // Span-based work pulled into its own non-async method - ref structs
    // (Span<T>/ReadOnlySpan<T>) can't be used as locals inside an async
    // method body in C# 12 (the compiler can't prove they never live across
    // an await), so this stays synchronous and DecryptAsync just awaits the
    // key first, then calls straight into this.
    internal static string DecryptPacked(string packedBase64, byte[] key)
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

        var derived = await DerivePeerKeyAsync(otherUserId, "voiceover-dm");
        if (derived is not null) _derivedPairKeyCache[otherUserId] = derived;
        return derived;
    }

    // Shared ECDH+HKDF derivation between this device and otherUserId - used
    // both for DM message keys (infoPrefix "voiceover-dm") and for server-key
    // wrap keys (infoPrefix includes the server id, see ServerKeyWrapInfoPrefix).
    // otherUserId may equal this account's own id (a "self" derivation, used
    // to wrap a server key for storage under a key only this account's own
    // private key can reproduce) - ECDH(myPriv, myPub) is well-defined and
    // only computable by whoever holds the private key, so this works
    // unmodified for that case too.
    private async Task<byte[]?> DerivePeerKeyAsync(int otherUserId, string infoPrefix)
    {
        if (_privateKey is null) return null;

        if (!_peerPublicKeyCache.TryGetValue(otherUserId, out var peerPublicKeyBytes))
        {
            var publicKeyBase64 = await _api.GetPublicKeyAsync(otherUserId);
            if (publicKeyBase64 is null) return null;

            try
            {
                peerPublicKeyBytes = Convert.FromBase64String(publicKeyBase64);
            }
            catch (FormatException)
            {
                // Shouldn't happen (the server only ever stores what a
                // client uploaded as a real SPKI-encoded key), but this
                // value came over the network - never let a malformed
                // blob throw out of an async void event handler and take
                // the whole process down with it.
                return null;
            }

            _peerPublicKeyCache[otherUserId] = peerPublicKeyBytes;
        }

        // Only the actual crypto section is serialized - ECDiffieHellman
        // wraps a native handle not documented as safe for concurrent use,
        // and _privateKey is shared across every concurrent caller.
        await _cryptoLock.WaitAsync();
        try
        {
            using var peerEcdh = ECDiffieHellman.Create();
            peerEcdh.ImportSubjectPublicKeyInfo(peerPublicKeyBytes, out _);

            var sharedSecret = _privateKey.DeriveRawSecretAgreement(peerEcdh.PublicKey);

            // Sorted so both participants build the identical info string
            // regardless of who's "self" and who's "other" for a given call.
            var myUserId = _api.CurrentUserId!.Value;
            var (loId, hiId) = myUserId < otherUserId ? (myUserId, otherUserId) : (otherUserId, myUserId);
            var info = Encoding.UTF8.GetBytes($"{infoPrefix}:{loId}:{hiId}");

            return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, outputLength: 32, salt: null, info: info);
        }
        catch (CryptographicException)
        {
            // Malformed/foreign public key bytes - fail closed instead of
            // crashing.
            return null;
        }
        finally
        {
            _cryptoLock.Release();
        }
    }

    private static byte[] DeriveWrappingKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);

    // internal (not private) so Client.Tests can exercise the actual AES-GCM
    // packing/unpacking logic directly with a known key, without needing to
    // mock the full ECDH handshake (ApiService network calls) that normally
    // derives it - see [assembly: InternalsVisibleTo] in AssemblyInfo.cs.
    internal static byte[] WrapBytes(byte[] plaintext, byte[] wrappingKey)
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
