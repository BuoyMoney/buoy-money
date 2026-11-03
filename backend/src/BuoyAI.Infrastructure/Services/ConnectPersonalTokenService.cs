using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.OAuth;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BuoyAI.Infrastructure.Services;

/// <inheritdoc cref="IConnectPersonalTokenService"/>
public class ConnectPersonalTokenService : IConnectPersonalTokenService
{
  private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);
  // Recording every call would write on every tool call; a few minutes is plenty for "last used".
  private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(5);
  private const int MaxNameLength = 60;
  private const int MaxActiveTokensPerUser = 20;
  // The same scopes an OAuth-connected assistant gets: read, plus remembering notes.
  private const string Scope = "buoyai:read buoyai:write:memory";

  private readonly IDbContextFactory<Data.BuoyAIDbContext> _contextFactory;
  private readonly McpOptions _options;
  private readonly string _signingKey;

  public ConnectPersonalTokenService(
    IDbContextFactory<Data.BuoyAIDbContext> contextFactory,
    IOptions<McpOptions> options,
    IConfiguration configuration)
  {
    _contextFactory = contextFactory;
    _options = options.Value;
    _signingKey = BuoyAI.Infrastructure.Configuration.StartupSecretsValidator.ResolveMcpSigningKey(configuration);
  }

  public async Task<IReadOnlyList<ConnectPersonalTokenDto>> ListAsync(int userId, CancellationToken ct)
  {
    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    var now = DateTime.UtcNow;
    var rows = await ctx.ConnectPersonalTokens
      .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
      .OrderByDescending(t => t.CreatedAt)
      .ToListAsync(ct);
    return rows.Select(ToDto).ToList();
  }

  public async Task<CreatedConnectPersonalTokenDto> CreateAsync(int userId, string name, CancellationToken ct)
  {
    var issuer = (_options.Issuer ?? throw new InvalidOperationException("Mcp:Issuer not configured")).TrimEnd('/');
    var resource = _options.ResourceUri ?? throw new InvalidOperationException("Mcp:ResourceUri not configured");

    name = name.Trim();
    if (name.Length == 0) throw new ArgumentException("Give the token a name.");
    if (name.Length > MaxNameLength) name = name[..MaxNameLength];

    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    var now = DateTime.UtcNow;
    var active = await ctx.ConnectPersonalTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now, ct);
    if (active >= MaxActiveTokensPerUser)
      throw new InvalidOperationException($"You have {MaxActiveTokensPerUser} tokens already. Revoke one you no longer use first.");

    var row = new ConnectPersonalToken
    {
      Id = Guid.NewGuid(),
      UserId = userId,
      Name = name,
      CreatedAt = now,
      ExpiresAt = now.Add(Lifetime),
    };
    ctx.ConnectPersonalTokens.Add(row);
    await ctx.SaveChangesAsync(ct);

    var descriptor = new SecurityTokenDescriptor
    {
      Subject = new ClaimsIdentity(new[]
      {
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim("scope", Scope),
        new Claim("client_id", IConnectPersonalTokenService.ClientId),
        new Claim(JwtRegisteredClaimNames.Jti, row.Id.ToString()),
      }),
      NotBefore = now,
      Expires = row.ExpiresAt,
      SigningCredentials = new SigningCredentials(
        new SymmetricSecurityKey(Encoding.ASCII.GetBytes(_signingKey)), SecurityAlgorithms.HmacSha256Signature),
      // Same pinned issuer and audience as an OAuth Connect token: valid on /mcp, never on the app's own API.
      Issuer = issuer,
      Audience = resource,
    };
    var handler = new JwtSecurityTokenHandler();
    var token = handler.WriteToken(handler.CreateToken(descriptor));

    var dto = ToDto(row);
    return new CreatedConnectPersonalTokenDto
    {
      Id = dto.Id,
      Name = dto.Name,
      CreatedAt = dto.CreatedAt,
      ExpiresAt = dto.ExpiresAt,
      LastUsedAt = dto.LastUsedAt,
      Token = token,
      McpUrl = resource,
    };
  }

  public async Task<bool> RevokeAsync(int userId, Guid id, CancellationToken ct)
  {
    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    var row = await ctx.ConnectPersonalTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
    if (row == null) return false;
    row.RevokedAt ??= DateTime.UtcNow;
    await ctx.SaveChangesAsync(ct);
    return true;
  }

  public async Task<bool> ValidateAsync(Guid id, int userId, CancellationToken ct)
  {
    await using var ctx = await _contextFactory.CreateDbContextAsync(ct);
    var row = await ctx.ConnectPersonalTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (row == null || row.UserId != userId || !row.IsActive) return false;

    var now = DateTime.UtcNow;
    if (row.LastUsedAt == null || now - row.LastUsedAt.Value > LastUsedGranularity)
    {
      row.LastUsedAt = now;
      await ctx.SaveChangesAsync(ct);
    }
    return true;
  }

  private static ConnectPersonalTokenDto ToDto(ConnectPersonalToken t) => new()
  {
    Id = t.Id,
    Name = t.Name,
    CreatedAt = t.CreatedAt,
    ExpiresAt = t.ExpiresAt,
    LastUsedAt = t.LastUsedAt,
  };
}
