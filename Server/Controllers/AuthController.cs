using System.Security.Claims;
using Voiceover.Server.Auth;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
using Voiceover.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Voiceover.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly JwtTokenService _jwt;
    private readonly TwoFactorService _twoFactor;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext db, JwtTokenService jwt, TwoFactorService twoFactor, ILogger<AuthController> logger)
    {
        _db = db;
        _jwt = jwt;
        _twoFactor = twoFactor;
        _logger = logger;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest("Username and password are required.");

        if (req.Password.Length < 8)
            return BadRequest("Password must be at least 8 characters.");

        if (await _db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict("Username already taken.");

        var user = new User
        {
            Username = req.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(await IssueTokensAsync(user));
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            // Username only, never the password - a string of these for the
            // same username/IP in a short window is the actual brute-force
            // signal worth alerting on, on top of the rate limiter already
            // throttling the requests themselves.
            _logger.LogWarning("Failed login attempt for {Username} from {RemoteIp}",
                req.Username, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Invalid username or password.");
        }

        if (user.TwoFactorEnabled)
        {
            var challengeToken = _jwt.CreateTotpChallengeToken(user.Id);
            return Ok(new LoginResponse(true, challengeToken));
        }

        var auth = await IssueTokensAsync(user);
        return Ok(new LoginResponse(false, null, auth.Token, auth.ExpiresAtUtc, auth.RefreshToken,
            auth.UserId, auth.Username, auth.AvatarUrl, auth.CustomStatus, auth.IsAdmin, auth.TwoFactorEnabled));
    }

    // Second step of a 2FA login - takes the challenge token Login just
    // returned plus either a live authenticator code or a recovery code.
    // No [Authorize]: the whole point is completing auth before any real
    // token exists yet, same reasoning as Refresh below. The challenge
    // token is validated manually (JwtTokenService.ValidateTotpChallengeToken)
    // rather than through the normal bearer pipeline, since a real access
    // token would otherwise also pass plain signature/issuer/audience/
    // lifetime checks - only that manual check looks at the "purpose" claim
    // distinguishing a challenge token from a real one.
    [HttpPost("login/totp")]
    public async Task<ActionResult<AuthResponse>> LoginTotp(TotpLoginRequest req)
    {
        var userId = _jwt.ValidateTotpChallengeToken(req.ChallengeToken);
        if (userId is null) return Unauthorized("Invalid or expired challenge.");

        var user = await _db.Users.FindAsync(userId.Value);
        if (user is null || !user.TwoFactorEnabled || user.TotpSecret is null) return Unauthorized("Invalid or expired challenge.");

        var verified = false;
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            verified = _twoFactor.VerifyCode(user.TotpSecret, req.Code);
        }
        else if (!string.IsNullOrWhiteSpace(req.RecoveryCode))
        {
            var userCodes = await _db.TotpRecoveryCodes.Where(c => c.UserId == user.Id).ToListAsync();
            verified = _twoFactor.TryConsumeRecoveryCode(userCodes, req.RecoveryCode);
            if (verified) await _db.SaveChangesAsync();
        }

        if (!verified)
        {
            _logger.LogWarning("Failed 2FA code attempt for user {UserId} from {RemoteIp}",
                user.Id, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Invalid code.");
        }

        return Ok(await IssueTokensAsync(user));
    }

    // No [Authorize] - deliberately. The whole point of this endpoint is to
    // get a new access token once the old one has already expired, so it
    // can't require a still-valid access token to call it. Auth here comes
    // from possessing a valid, unexpired, unrevoked refresh token instead.
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest req)
    {
        var hash = JwtTokenService.HashRefreshToken(req.RefreshToken);
        var existing = await _db.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.TokenHash == hash);

        if (existing is null || existing.RevokedAt is not null || existing.ExpiresAt <= DateTime.UtcNow || existing.User is null)
        {
            // Never logs the token itself (or its hash) - just that a
            // rejection happened and from where. A revoked-token reuse in
            // particular is worth noticing: it means whoever's calling this
            // has a refresh token that was already rotated out from under
            // them, which is the exact signature of two parties (a
            // legitimate client and a copy of its token) racing to use it.
            _logger.LogWarning("Rejected refresh token attempt from {RemoteIp}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized("Refresh token is invalid or expired.");
        }

        // Rotate rather than reuse: revoke the token that was just spent and
        // issue a brand new one. Limits how long a copied-but-not-yet-used
        // refresh token stays valid for whoever copied it, and makes reuse
        // of an already-rotated token detectable (it'll just be revoked).
        existing.RevokedAt = DateTime.UtcNow;

        return Ok(await IssueTokensAsync(existing.User));
    }

    // Revokes only the one session (device/install) this refresh token
    // belongs to - the normal "log out" action.
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest req)
    {
        var hash = JwtTokenService.HashRefreshToken(req.RefreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == hash);
        if (existing is not null && existing.RevokedAt is null)
        {
            existing.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok();
    }

    // The actual "kill switch" for a suspected-stolen token: revokes every
    // active refresh token for the current user, not just the caller's own
    // session. Needs a still-valid access token to call (unlike Refresh
    // above) - if an attacker only has a stolen access token and not the
    // refresh token, this still lets the legitimate user lock them out
    // everywhere once that access token expires within the hour.
    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll()
    {
        var userId = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var active = await _db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ToListAsync();

        foreach (var token in active)
            token.RevokedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok();
    }

    // --- TOTP 2FA enrollment (Settings > My Account) ---

    // Generates a fresh secret and returns it (QR + manual-entry form) but
    // does NOT enable 2FA yet - Confirm2fa below is what actually flips
    // TwoFactorEnabled, once the user proves they scanned it correctly.
    // Calling this again before confirming just overwrites the pending
    // secret, which is fine - nothing is enabled until confirmed either way.
    [HttpPost("2fa/setup")]
    [Authorize]
    public async Task<ActionResult<TotpSetupResponse>> Setup2fa()
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();

        var secret = _twoFactor.GenerateSecret();
        user.TotpSecret = secret;
        user.TwoFactorEnabled = false;
        await _db.SaveChangesAsync();

        var otpAuthUri = _twoFactor.BuildOtpAuthUri(user.Username, secret);
        var qrPng = _twoFactor.BuildQrCodePng(otpAuthUri);
        return Ok(new TotpSetupResponse(secret, Convert.ToBase64String(qrPng)));
    }

    // Verifies the user actually has the secret working in their
    // authenticator app before turning 2FA on for real, then issues the
    // one-time recovery codes.
    [HttpPost("2fa/confirm")]
    [Authorize]
    public async Task<ActionResult<TotpConfirmResponse>> Confirm2fa(TotpConfirmRequest req)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user?.TotpSecret is null) return BadRequest("Call 2fa/setup first.");

        if (!_twoFactor.VerifyCode(user.TotpSecret, req.Code))
            return BadRequest("Invalid code.");

        user.TwoFactorEnabled = true;

        // Replace any leftover codes from a previous enable/disable cycle -
        // a fresh confirm always gets a fresh set.
        var existing = await _db.TotpRecoveryCodes.Where(c => c.UserId == user.Id).ToListAsync();
        _db.TotpRecoveryCodes.RemoveRange(existing);

        var (rawCodes, rows) = _twoFactor.GenerateRecoveryCodes(user.Id);
        _db.TotpRecoveryCodes.AddRange(rows);

        await _db.SaveChangesAsync();
        return Ok(new TotpConfirmResponse(rawCodes));
    }

    // Requires re-entering the current password - same "prove you're still
    // you" bar as any other security-downgrading action, since this is
    // reachable from an already-open session that could otherwise be an
    // unattended, unlocked machine.
    [HttpPost("2fa/disable")]
    [Authorize]
    public async Task<ActionResult> Disable2fa(TotpDisableRequest req)
    {
        var user = await _db.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();
        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash)) return Unauthorized("Incorrect password.");

        user.TotpSecret = null;
        user.TwoFactorEnabled = false;

        var codes = await _db.TotpRecoveryCodes.Where(c => c.UserId == user.Id).ToListAsync();
        _db.TotpRecoveryCodes.RemoveRange(codes);

        await _db.SaveChangesAsync();
        return Ok();
    }

    private async Task<AuthResponse> IssueTokensAsync(User user)
    {
        var (accessToken, expiresAtUtc) = _jwt.CreateAccessToken(user.Id, user.Username);

        var rawRefreshToken = JwtTokenService.GenerateRefreshToken();
        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = JwtTokenService.HashRefreshToken(rawRefreshToken),
            ExpiresAt = DateTime.UtcNow.Add(JwtTokenService.RefreshTokenLifetime)
        });
        await _db.SaveChangesAsync();

        return new AuthResponse(accessToken, expiresAtUtc, rawRefreshToken, user.Id, user.Username, user.AvatarUrl, user.CustomStatus, user.IsAdmin, user.TwoFactorEnabled);
    }
}
