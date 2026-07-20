using System.Security.Cryptography;
using OtpNet;
using QRCoder;
using Voiceover.Server.Models;

namespace Voiceover.Server.Services;

// TOTP (RFC 6238) enrollment/verification for AuthController - the same
// standard every authenticator app (Microsoft Authenticator, Google
// Authenticator, Authy, ...) implements identically, so nothing here is
// tied to any one of them. Plain scoped-free static-ish service (no DB
// access of its own - AuthController owns reading/writing User.TotpSecret/
// TwoFactorEnabled and TotpRecoveryCode rows), matching this codebase's
// existing "thin service, not much state" convention.
public class TwoFactorService
{
    private const int RecoveryCodeCount = 10;

    public string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    // otpauth:// URI encoded into the enrollment QR - "Voiceover" as both
    // the issuer and the label prefix is what shows up next to the entry
    // inside the authenticator app.
    public string BuildOtpAuthUri(string username, string secret) =>
        $"otpauth://totp/Voiceover:{Uri.EscapeDataString(username)}?secret={secret}&issuer=Voiceover";

    // PngByteQRCode (not QRCode/BitmapByteQRCode) deliberately - those
    // render through System.Drawing.Bitmap, which needs libgdiplus and
    // isn't reliably available on Railway's Linux container; this renderer
    // is pure managed code, PNG bytes straight out.
    public byte[] BuildQrCodePng(string otpAuthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(10);
    }

    // +/-1 time step (30s each) tolerance for clock drift between this
    // server and the user's phone - Otp.NET's own documented default for
    // "reasonable" verification windows.
    public bool VerifyCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: 1, future: 1));
    }

    // Raw codes are returned once for the caller to show the user - only
    // the BCrypt hashes (same algorithm as PasswordHash) are meant to be
    // persisted.
    public (List<string> RawCodes, List<TotpRecoveryCode> Rows) GenerateRecoveryCodes(int userId)
    {
        var rawCodes = new List<string>();
        var rows = new List<TotpRecoveryCode>();

        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var code = GenerateRecoveryCode();
            rawCodes.Add(code);
            rows.Add(new TotpRecoveryCode
            {
                UserId = userId,
                CodeHash = BCrypt.Net.BCrypt.HashPassword(code)
            });
        }

        return (rawCodes, rows);
    }

    // Format: xxxx-xxxx, base32-alphabet-ish (no ambiguous 0/O/1/I) - easy
    // to read back off a printed/saved list without transcription errors.
    private static string GenerateRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < 9; i++)
        {
            if (i == 4) { chars[i] = '-'; continue; }
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return new string(chars);
    }

    // Finds and marks-used the first still-unused code among userCodes
    // whose hash matches suppliedCode - each code works exactly once.
    // Caller is responsible for saving the DB change.
    public bool TryConsumeRecoveryCode(List<TotpRecoveryCode> userCodes, string suppliedCode)
    {
        if (string.IsNullOrWhiteSpace(suppliedCode)) return false;

        var match = userCodes.FirstOrDefault(c =>
            c.UsedAt is null && BCrypt.Net.BCrypt.Verify(suppliedCode.Trim(), c.CodeHash));
        if (match is null) return false;

        match.UsedAt = DateTime.UtcNow;
        return true;
    }
}
