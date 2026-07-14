using System.Security.Cryptography;
using System.Text;
using Voiceover.Client.Services;

namespace Client.Tests;

// Exercises E2eeService's actual AES-256-GCM pack/unpack core (WrapBytes/
// DecryptPacked, made internal + visible to this assembly via
// [assembly: InternalsVisibleTo] in Client/AssemblyInfo.cs) directly against
// a known key, instead of the full ECDH handshake that normally derives it -
// that handshake goes through ApiService (network calls to fetch a peer's
// public key), which would need a much heavier mock setup to exercise here.
// This still tests the exact framing/encrypt/decrypt logic every real DM
// and channel message goes through.
public class E2eeServiceCryptoTests
{
    [Fact]
    public void WrapThenDecrypt_RoundTrips_ToOriginalPlaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = "hello, this is a test message with unicode: héllo 👋";
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        var packed = E2eeService.WrapBytes(plaintextBytes, key);
        var packedBase64 = Convert.ToBase64String(packed);

        var decrypted = E2eeService.DecryptPacked(packedBase64, key);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void WrapThenDecrypt_EmptyPlaintext_RoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        var packed = E2eeService.WrapBytes(Array.Empty<byte>(), key);
        var decrypted = E2eeService.DecryptPacked(Convert.ToBase64String(packed), key);

        Assert.Equal("", decrypted);
    }

    [Fact]
    public void Decrypt_WithWrongKey_ThrowsInsteadOfReturningGarbageSilently()
    {
        var rightKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var packed = E2eeService.WrapBytes(Encoding.UTF8.GetBytes("secret"), rightKey);
        var packedBase64 = Convert.ToBase64String(packed);

        // AES-GCM's authentication tag makes this fail closed (throws)
        // rather than silently decrypting to wrong bytes - callers
        // (DecryptForServerAsync/DecryptAsync) rely on exactly this to show
        // "[Unable to decrypt message]" instead of corrupted text.
        // AesGcm.Decrypt throws the more specific AuthenticationTagMismatchException
        // (a CryptographicException subclass, added in .NET 8) rather than
        // the base type - E2eeService's own catch clauses already handle
        // this correctly by catching CryptographicException, but
        // Assert.Throws<T> requires an exact type match, not "is a".
        Assert.Throws<AuthenticationTagMismatchException>(() => E2eeService.DecryptPacked(packedBase64, wrongKey));
    }

    [Fact]
    public void Decrypt_TruncatedCiphertext_ThrowsArgumentException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        // Shorter than NonceSizeBytes(12) + TagSizeBytes(16) = 28 bytes.
        var tooShort = Convert.ToBase64String(new byte[10]);

        Assert.Throws<ArgumentException>(() => E2eeService.DecryptPacked(tooShort, key));
    }

    [Fact]
    public void Wrap_ProducesDifferentCiphertext_ForSamePlaintextAndKey()
    {
        // Nonce is freshly randomized per call (RandomNumberGenerator.GetBytes
        // inside WrapBytes) - encrypting the same message twice must never
        // produce identical ciphertext, or an eavesdropper could correlate
        // repeated messages without ever breaking the key.
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("same message");

        var packed1 = E2eeService.WrapBytes(plaintext, key);
        var packed2 = E2eeService.WrapBytes(plaintext, key);

        Assert.NotEqual(Convert.ToBase64String(packed1), Convert.ToBase64String(packed2));
    }
}
