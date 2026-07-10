using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Voiceover.Server.Auth;

public class JwtTokenService
{
    private readonly string _key;
    private readonly string _issuer;

    public JwtTokenService(IConfiguration config)
    {
        // In production, put this in user-secrets or environment variables,
        // never commit a real signing key to source control.
        _key = config["Jwt:Key"] ?? "dev-only-signing-key-change-me-please-1234567890";
        _issuer = config["Jwt:Issuer"] ?? "VoiceoverServer";
    }

    public string CreateToken(int userId, string username)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username)
        };

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _issuer,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

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
