using Voiceover.Server.Auth;
using Voiceover.Server.Data;
using Voiceover.Server.Dtos;
using Voiceover.Server.Models;
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

    public AuthController(AppDbContext db, JwtTokenService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest("Username and password are required.");

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
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized("Invalid username or password.");

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
            return Unauthorized("Refresh token is invalid or expired.");

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

        return new AuthResponse(accessToken, expiresAtUtc, rawRefreshToken, user.Id, user.Username, user.AvatarUrl);
    }
}
