using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Mazda.Application.Common.Abstractions;

namespace Mazda.Infrastructure.Security;

public class JwtOptions
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "mazda";
    public string Audience { get; set; } = "mazda";
    public int ExpiryMinutes { get; set; } = 60 * 24 * 7; // 7 days
}

public class BCryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);
    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}

public class JwtIssuer(IOptions<JwtOptions> opts) : IJwtIssuer
{
    public string Issue(Guid userId, string email)
    {
        var o = opts.Value;
        if (string.IsNullOrWhiteSpace(o.Secret) || o.Secret.Length < 32)
            throw new InvalidOperationException("JWT secret must be set and at least 32 chars.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: o.Issuer,
            audience: o.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(o.ExpiryMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
