using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BuoyAI.Core.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BuoyAI.Infrastructure.Services.CloudPortal;

/// <summary>
/// Signs the owner tokens the Cloud pairing API accepts (OwnerAuthentication.cs in the Cloud pairing library under cloud/):
/// HS256 with <c>CloudPortal:OwnerJwtKey</c>, iss/aud from options, <c>sub</c> = the Cloud account id, and
/// <c>mfa</c> = "true" only when the caller passed step-up MFA. One token per request, valid five minutes.
/// </summary>
public sealed class CloudOwnerTokenIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly CloudPortalOptions _options;
    private readonly TimeProvider _time;

    public CloudOwnerTokenIssuer(IOptions<CloudPortalOptions> options, TimeProvider? time = null)
    {
        _options = options.Value;
        _time = time ?? TimeProvider.System;
    }

    public string Mint(string ownerId, bool mfa)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("An owner id is required.", nameof(ownerId));
        if (!_options.IsConfigured) throw new InvalidOperationException("CloudPortal:OwnerJwtKey is not set.");

        var now = _time.GetUtcNow().UtcDateTime;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, ownerId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (mfa) claims.Add(new Claim("mfa", "true"));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.OwnerJwtKey));
        var header = new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var payload = new JwtPayload(_options.Issuer, _options.Audience, claims, now, now + Lifetime, now);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }
}
