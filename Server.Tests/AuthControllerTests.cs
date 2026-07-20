using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OtpNet;
using Voiceover.Server.Auth;
using Voiceover.Server.Controllers;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
using Voiceover.Server.Services;

namespace Server.Tests;

// First-ever test coverage for AuthController - the highest-value tests in
// this batch, since login (with or without 2FA) is the most security-
// critical path in the whole app and had zero coverage before this.
public class AuthControllerTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    // Empty IConfiguration is fine - JwtTokenService falls back to its own
    // dev-only defaults for Jwt:Key/Jwt:Issuer when they're not configured.
    private static JwtTokenService CreateJwt() => new(new ConfigurationBuilder().Build());

    private static AuthController CreateController(AppDbContext db, JwtTokenService? jwt = null, TwoFactorService? twoFactor = null)
    {
        var controller = new AuthController(db, jwt ?? CreateJwt(), twoFactor ?? new TwoFactorService(), NullLogger<AuthController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    [Fact]
    public async Task Login_WithoutTwoFactor_ReturnsCompletedLogin_NotAChallenge()
    {
        var db = CreateDb();
        var user = new User { Username = "alice", PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123") };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await CreateController(db).Login(new LoginRequest("alice", "password123"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<LoginResponse>(ok.Value);
        Assert.False(response.RequiresTwoFactor);
        Assert.Null(response.ChallengeToken);
        Assert.NotNull(response.Token);
        Assert.Equal(user.Id, response.UserId);
    }

    [Fact]
    public async Task Login_RejectsWrongPassword()
    {
        var db = CreateDb();
        var user = new User { Username = "alice", PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123") };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await CreateController(db).Login(new LoginRequest("alice", "wrongpassword"));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Login_WithTwoFactorEnabled_ReturnsChallenge_NeverRealTokens()
    {
        var db = CreateDb();
        var secret = new TwoFactorService().GenerateSecret();
        var user = new User
        {
            Username = "bob",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("password123"),
            TwoFactorEnabled = true,
            TotpSecret = secret
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var result = await CreateController(db).Login(new LoginRequest("bob", "password123"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<LoginResponse>(ok.Value);
        Assert.True(response.RequiresTwoFactor);
        Assert.NotNull(response.ChallengeToken);
        Assert.Null(response.Token);
    }

    [Fact]
    public async Task LoginTotp_SucceedsWithValidCode_AndIssuesRealTokens()
    {
        var db = CreateDb();
        var secret = new TwoFactorService().GenerateSecret();
        var user = new User { Username = "carol", PasswordHash = "x", TwoFactorEnabled = true, TotpSecret = secret };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var jwt = CreateJwt();
        var controller = CreateController(db, jwt);
        var challengeToken = jwt.CreateTotpChallengeToken(user.Id);
        var code = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();

        var result = await controller.LoginTotp(new TotpLoginRequest(challengeToken, code, null));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var auth = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal(user.Id, auth.UserId);
    }

    [Fact]
    public async Task LoginTotp_RejectsAWrongCode()
    {
        var db = CreateDb();
        var secret = new TwoFactorService().GenerateSecret();
        var user = new User { Username = "dave", PasswordHash = "x", TwoFactorEnabled = true, TotpSecret = secret };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var jwt = CreateJwt();
        var controller = CreateController(db, jwt);
        var challengeToken = jwt.CreateTotpChallengeToken(user.Id);

        var result = await controller.LoginTotp(new TotpLoginRequest(challengeToken, "000000", null));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task LoginTotp_RejectsAGarbageOrExpiredChallengeToken()
    {
        var db = CreateDb();

        var result = await CreateController(db).LoginTotp(new TotpLoginRequest("not-a-real-token", "123456", null));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task LoginTotp_RejectsARealAccessToken_NotJustAChallengeToken()
    {
        // The whole reason ValidateTotpChallengeToken checks a "purpose"
        // claim instead of relying on [Authorize]'s normal bearer
        // validation - a real access token passes plain signature/issuer/
        // audience/lifetime checks just as well as a challenge token would.
        var db = CreateDb();
        var jwt = CreateJwt();
        var (realAccessToken, _) = jwt.CreateAccessToken(1, "someone");

        var result = await CreateController(db, jwt).LoginTotp(new TotpLoginRequest(realAccessToken, "123456", null));

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task LoginTotp_ConsumesARecoveryCode_ExactlyOnce()
    {
        var db = CreateDb();
        var user = new User { Username = "erin", PasswordHash = "x", TwoFactorEnabled = true, TotpSecret = "AAAAAAAAAAAAAAAA" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var twoFactor = new TwoFactorService();
        var (rawCodes, rows) = twoFactor.GenerateRecoveryCodes(user.Id);
        db.TotpRecoveryCodes.AddRange(rows);
        await db.SaveChangesAsync();

        var jwt = CreateJwt();
        var controller = CreateController(db, jwt, twoFactor);
        var challengeToken = jwt.CreateTotpChallengeToken(user.Id);
        var recoveryCode = rawCodes[0];

        var first = await controller.LoginTotp(new TotpLoginRequest(challengeToken, null, recoveryCode));
        Assert.IsType<OkObjectResult>(first.Result);

        var second = await controller.LoginTotp(new TotpLoginRequest(challengeToken, null, recoveryCode));
        Assert.IsType<UnauthorizedObjectResult>(second.Result);
    }
}
