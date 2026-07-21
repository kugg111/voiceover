using System.Security.Cryptography;
using System.Text;

// Cross-platform E2EE crypto check for the Linux client plan, Phase 0.
// E2eeService.cs's crypto (ECDH P-256, HKDF-SHA256, PBKDF2-SHA256,
// AES-256-GCM) is all .NET BCL, officially cross-platform - but "the BCL
// is portable" isn't proof of interop, and this code decrypts messages
// from Windows clients, so it has to be exact. These constants were
// generated once on Windows (fixed inputs, not freshly random each run)
// by re-deriving every step from the SAME fixed inputs and comparing
// exact bytes - this asserts a Linux run reproduces them all identically.
//
// This mirrors E2eeService's exact algorithm calls rather than importing
// the class directly, since Client.Core (the planned shared library that
// will make E2eeService itself referenceable from non-Windows projects)
// doesn't exist yet - this check is what justifies building it. Replace
// with a direct E2eeService call once that extraction happens.
internal static class E2eeVectorCheck
{
    // --- Fixed inputs + expected outputs, generated once on Windows ---
    private const string AlicePrivPkcs8Base64 = "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgeoEDBeZhXtYiy4XfnOsamJRF5sA3lXlljb2S8NJd2kihRANCAASZRxn1M2u3aI8m3cobPsiuidJAIJaBAav9j/nFK7y+v4zj3wfsePybR90dKYlpWk7vilFRkmi4CUbggKUDaFOR";
    private const string BobPubSpkiBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4yYt41fzkBsZZGArPK54dlb48KiAWh9tKkhs09HGsr3dOzJMo2xp/Z+EdDn+uT9V3K3lgjqbb9N/8MpykAO7QQ==";
    private const string ExpectedSharedSecretBase64 = "i1k7P0lgKPbiDeokZZIzpcOebuUfASdg4ressmGG3Qg=";
    private const string ExpectedHkdfKeyBase64 = "11BqnVI8koJoKgOtJnDRKajNvVzDtGNfQRalbd0gxr0=";
    private const string ExpectedPbkdf2KeyBase64 = "AAjmm4n/rBqnux9EKJumWvqnEd1FDwqrbDIuTNV7shY=";
    private const string ExpectedPackedCiphertextBase64 = "EBESExQVFhcYGRobhoP+ee1aCzdE2mboWhOad2qru3k0c/AVT5AfnXfzVguU4++Ghch9S/YTuUDFxmz+D9xkR7Jw8OX16GBM1339BKHgeBPLzTESGeK/Ng==";
    private const string ExpectedPlaintext = "Hello from the cross-platform E2EE test vector! éèê你好";

    public static bool Run()
    {
        var ok = true;
        ok &= Check("ECDH shared secret", CheckSharedSecret);
        ok &= Check("HKDF-SHA256 derived key", CheckHkdf);
        ok &= Check("PBKDF2-SHA256 derived key", CheckPbkdf2);
        ok &= Check("AES-256-GCM decrypt (Windows-encrypted blob)", CheckAesGcmDecrypt);
        ok &= Check("AES-256-GCM encrypt (byte-for-byte match)", CheckAesGcmEncrypt);
        return ok;
    }

    private static bool Check(string name, Func<bool> check)
    {
        try
        {
            var pass = check();
            Console.WriteLine(pass ? $"  [OK] {name}" : $"  [FAIL] {name}");
            return pass;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static byte[] SharedSecret()
    {
        using var alice = ECDiffieHellman.Create();
        alice.ImportPkcs8PrivateKey(Convert.FromBase64String(AlicePrivPkcs8Base64), out _);
        using var bobPub = ECDiffieHellman.Create();
        bobPub.ImportSubjectPublicKeyInfo(Convert.FromBase64String(BobPubSpkiBase64), out _);
        return alice.DeriveRawSecretAgreement(bobPub.PublicKey);
    }

    private static bool CheckSharedSecret() =>
        Convert.ToBase64String(SharedSecret()) == ExpectedSharedSecretBase64;

    private static bool CheckHkdf()
    {
        var info = Encoding.UTF8.GetBytes("voiceover-dm:1:2");
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, SharedSecret(), outputLength: 32, salt: null, info: info);
        return Convert.ToBase64String(key) == ExpectedHkdfKeyBase64;
    }

    private static byte[] Pbkdf2Key()
    {
        var salt = Convert.FromBase64String("AQIDBAUGBwgJCgsMDQ4PEA==");
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes("correct horse battery staple"), salt, 600_000, HashAlgorithmName.SHA256, 32);
    }

    private static bool CheckPbkdf2() => Convert.ToBase64String(Pbkdf2Key()) == ExpectedPbkdf2KeyBase64;

    private static bool CheckAesGcmDecrypt()
    {
        const int nonceSize = 12, tagSize = 16;
        var packed = Convert.FromBase64String(ExpectedPackedCiphertextBase64);
        var nonce = packed.AsSpan(0, nonceSize);
        var tag = packed.AsSpan(nonceSize, tagSize);
        var ciphertext = packed.AsSpan(nonceSize + tagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(Pbkdf2Key(), tagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext) == ExpectedPlaintext;
    }

    private static bool CheckAesGcmEncrypt()
    {
        const int nonceSize = 12, tagSize = 16;
        var plaintext = Encoding.UTF8.GetBytes(ExpectedPlaintext);
        var nonce = Convert.FromBase64String("EBESExQVFhcYGRob"); // same fixed nonce used to generate the vector
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[tagSize];

        using var aesGcm = new AesGcm(Pbkdf2Key(), tagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var packed = new byte[nonceSize + tagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonceSize);
        Buffer.BlockCopy(tag, 0, packed, nonceSize, tagSize);
        Buffer.BlockCopy(ciphertext, 0, packed, nonceSize + tagSize, ciphertext.Length);
        return Convert.ToBase64String(packed) == ExpectedPackedCiphertextBase64;
    }
}
