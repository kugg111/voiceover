using OtpNet;
using Voiceover.Server.Models;
using Voiceover.Server.Services;

namespace Server.Tests;

public class TwoFactorServiceTests
{
    [Fact]
    public void VerifyCode_AcceptsTheCurrentCode()
    {
        var service = new TwoFactorService();
        var secret = service.GenerateSecret();
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        Assert.True(service.VerifyCode(secret, code));
    }

    [Fact]
    public void VerifyCode_RejectsAWrongCode()
    {
        var service = new TwoFactorService();
        var secret = service.GenerateSecret();

        Assert.False(service.VerifyCode(secret, "000000"));
    }

    [Fact]
    public void VerifyCode_RejectsEmptyOrWhitespace()
    {
        var service = new TwoFactorService();
        var secret = service.GenerateSecret();

        Assert.False(service.VerifyCode(secret, ""));
        Assert.False(service.VerifyCode(secret, "   "));
    }

    [Fact]
    public void GenerateRecoveryCodes_ReturnsTenUniqueCodes_WithMatchingHashes()
    {
        var service = new TwoFactorService();

        var (rawCodes, rows) = service.GenerateRecoveryCodes(userId: 42);

        Assert.Equal(10, rawCodes.Count);
        Assert.Equal(10, rows.Count);
        Assert.Equal(10, rawCodes.Distinct().Count());
        Assert.All(rows, r => Assert.Equal(42, r.UserId));
        for (var i = 0; i < rawCodes.Count; i++)
            Assert.True(BCrypt.Net.BCrypt.Verify(rawCodes[i], rows[i].CodeHash));
    }

    [Fact]
    public void TryConsumeRecoveryCode_ConsumesExactlyOnce()
    {
        var service = new TwoFactorService();
        var (rawCodes, rows) = service.GenerateRecoveryCodes(userId: 1);
        var target = rawCodes[3];

        Assert.True(service.TryConsumeRecoveryCode(rows, target));
        Assert.NotNull(rows.First(r => BCrypt.Net.BCrypt.Verify(target, r.CodeHash)).UsedAt);

        // Second attempt with the same code must fail - it's already used.
        Assert.False(service.TryConsumeRecoveryCode(rows, target));
    }

    [Fact]
    public void TryConsumeRecoveryCode_RejectsAnUnknownCode()
    {
        var service = new TwoFactorService();
        var (_, rows) = service.GenerateRecoveryCodes(userId: 1);

        Assert.False(service.TryConsumeRecoveryCode(rows, "NOTAREAL-CODE"));
    }

    [Fact]
    public void BuildOtpAuthUri_EncodesUsernameAndSecret()
    {
        var service = new TwoFactorService();

        var uri = service.BuildOtpAuthUri("alice bob", "JBSWY3DPEHPK3PXP");

        Assert.StartsWith("otpauth://totp/Voiceover:", uri);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri);
        Assert.Contains("issuer=Voiceover", uri);
    }

    [Fact]
    public void BuildQrCodePng_ReturnsNonEmptyPngBytes()
    {
        var service = new TwoFactorService();

        var png = service.BuildQrCodePng("otpauth://totp/Voiceover:alice?secret=ABC&issuer=Voiceover");

        Assert.NotEmpty(png);
        // PNG magic bytes.
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }
}
