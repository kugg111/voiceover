using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Voiceover.Server.Auth;

public class JwtTokenService
{
    private readonly string _key;
    private readonly string _issuer;

    // Access tokens are short-lived and carry the actual auth claims;
    // refresh tokens are long-lived, opaque, DB-backed bearer strings whose
    // only job is minting a new access token (see AuthController's
    // /refresh, /logout, /logout-all). This bounds how long a stolen access
    // token is useful for, without forcing a full re-login every hour - and
    // unlike the JWT itself, a refresh token CAN be revoked server-side
    // (RefreshToken.RevokedAt), which a stateless JWT signature never could
    // be short of rotating the signing key and logging everyone out.
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    public JwtTokenService(IConfiguration config)
    {
        // In production, put this in user-secrets or environment variables,
        // never commit a real signing key to source control.
        _key = config["Jwt:Key"] ?? "dev-only-signing-key-change-me-please-1234567890";
        _issuer = config["Jwt:Issuer"] ?? "VoiceoverServer";
    }

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(int userId, string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username)
        };

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
            SecurityAlgorithms.HmacSha256);

        var expiresAtUtc = DateTime.UtcNow.Add(AccessTokenLifetime);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _issuer,
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    // Raw value goes to the client once and is never stored - only its hash
    // is (see RefreshToken.TokenHash). 256 bits of entropy, base64url so it
    // round-trips cleanly through JSON/query strings without escaping.
    public static string GenerateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public TokenValidationParameters GetValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = _issuer,
        ValidAudience = _issuer,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key))
    };
}
