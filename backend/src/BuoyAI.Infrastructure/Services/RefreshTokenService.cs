using System.Security.Cryptography;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Refresh-token rotation with reuse detection. The raw token is a 256-bit random
/// value; only its SHA-256 hash is stored. Every use rotates the token (old revoked,
/// replacement issued in the same family); presenting an already-revoked token means
/// a rotated/stolen token was replayed, so the whole family is revoked.
///
/// Each operation runs on its OWN short-lived <see cref="BuoyAIDbContext"/> obtained
/// from an <see cref="IDbContextFactory{TContext}"/> rather than the request-scoped
/// context. Refresh-token issuance/rotation happens at the tail of the login pipeline,
/// where the request-scoped context may still be finishing another operation (e.g. a
/// device-trust write); sharing it there caused intermittent
/// "A second operation was started on this DbContext instance…" 500s
/// (Sentry 7600799192). A dedicated context makes these writes race-free.
/// </summary>
public class RefreshTokenService : IRefreshTokenService
{
  private readonly IDbContextFactory<BuoyAIDbContext> _contextFactory;
  private readonly ILogger<RefreshTokenService> _logger;
  private readonly int _lifetimeDays;
  private readonly TimeSpan _reuseGraceWindow;

  public RefreshTokenService(
    IDbContextFactory<BuoyAIDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<RefreshTokenService> logger)
  {
    _contextFactory = contextFactory;
    _logger = logger;
    _lifetimeDays = configuration.GetValue<int?>("Jwt:RefreshTokenDays") ?? 30;
    _reuseGraceWindow = TimeSpan.FromSeconds(
      configuration.GetValue<int?>("Jwt:RefreshReuseGraceSeconds") ?? 60);
  }

  public async Task<IssuedRefreshToken> IssueAsync(int userId, Guid? family = null)
  {
    await using var context = await _contextFactory.CreateDbContextAsync();
    var (rawToken, expiresAt, resolvedFamily) = await AddTokenAsync(context, userId, family);
    return new IssuedRefreshToken(rawToken, expiresAt, resolvedFamily);
  }

  public async Task<RotatedRefreshToken?> RotateAsync(string rawToken)
  {
    if (string.IsNullOrWhiteSpace(rawToken))
      return null;

    await using var context = await _contextFactory.CreateDbContextAsync();

    var hash = Hash(rawToken);
    var existing = await context.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);

    if (existing == null)
      return null;

    // Reuse of an already-revoked token. Two very different causes share this shape:
    //
    //  1. A benign multi-tab race: browser tabs share the refresh cookie but not an
    //     in-flight lock, so when the access token expires two tabs refresh at once —
    //     the first rotates, the second replays the just-revoked token milliseconds
    //     later. This bricked real sessions (2026-07-18: users 4 and 8, dozens of
    //     falsely-revoked families).
    //  2. Actual replay of a stolen rotated token.
    //
    // Disambiguate by time and provenance (the Auth0-style "reuse interval"): a token
    // revoked BY ROTATION (it has a successor) and re-presented within a short grace
    // window gets a sibling token in the same family — each tab ends up holding its
    // own live leaf. Outside the window, or for tokens revoked by logout/mass-revoke
    // (no successor), reuse still nukes the whole family.
    if (existing.RevokedAt != null)
    {
      var revokedByRotation = existing.ReplacedByTokenHash != null;
      var withinGrace = DateTime.UtcNow - existing.RevokedAt.Value <= _reuseGraceWindow;
      if (revokedByRotation && withinGrace && DateTime.UtcNow < existing.ExpiresAt)
      {
        // Only branch a family that is still healthy — never resurrect one that a
        // real reuse detection (or logout-all) already revoked.
        var familyAlive = await context.RefreshTokens.AnyAsync(t =>
          t.Family == existing.Family && t.RevokedAt == null && t.ExpiresAt > DateTime.UtcNow);
        if (familyAlive)
        {
          var (graceRaw, graceExpiresAt, _) = await AddTokenAsync(context, existing.UserId, existing.Family);
          _logger.LogInformation(
            "Refresh-token grace rotation for user {UserId} (multi-tab race, family {Family}).",
            existing.UserId, existing.Family);
          return new RotatedRefreshToken(existing.UserId, graceRaw, graceExpiresAt);
        }
      }

      await RevokeFamilyAsync(context, existing.Family);
      _logger.LogWarning(
        "Refresh-token reuse detected for user {UserId}; revoked family {Family}.",
        existing.UserId, existing.Family);
      return null;
    }

    if (DateTime.UtcNow >= existing.ExpiresAt)
      return null;

    // Issue the replacement on the SAME context so rotation is a single unit of work.
    var (issuedRaw, issuedExpiresAt, _) = await AddTokenAsync(context, existing.UserId, existing.Family);

    existing.RevokedAt = DateTime.UtcNow;
    existing.ReplacedByTokenHash = Hash(issuedRaw);
    await context.SaveChangesAsync();

    return new RotatedRefreshToken(existing.UserId, issuedRaw, issuedExpiresAt);
  }

  public async Task RevokeAsync(string rawToken)
  {
    if (string.IsNullOrWhiteSpace(rawToken))
      return;

    await using var context = await _contextFactory.CreateDbContextAsync();

    var hash = Hash(rawToken);
    var existing = await context.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
    if (existing?.RevokedAt == null && existing != null)
    {
      existing.RevokedAt = DateTime.UtcNow;
      await context.SaveChangesAsync();
    }
  }

  public async Task<int> RevokeAllForUserAsync(int userId)
  {
    await using var context = await _contextFactory.CreateDbContextAsync();

    var now = DateTime.UtcNow;
    var active = await context.RefreshTokens
      .Where(t => t.UserId == userId && t.RevokedAt == null)
      .ToListAsync();
    foreach (var token in active)
      token.RevokedAt = now;
    if (active.Count > 0)
      await context.SaveChangesAsync();

    _logger.LogInformation("Revoked all {Count} refresh tokens for user {UserId}", active.Count, userId);
    return active.Count;
  }

  /// <summary>
  /// Inserts a fresh refresh token (persisting only its hash) on the supplied context and
  /// saves. Returns the raw token, its expiry, and the (possibly newly-created) family.
  /// </summary>
  private async Task<(string RawToken, DateTime ExpiresAt, Guid Family)> AddTokenAsync(
    BuoyAIDbContext context, int userId, Guid? family)
  {
    var rawToken = GenerateRawToken();
    var expiresAt = DateTime.UtcNow.AddDays(_lifetimeDays);
    var resolvedFamily = family ?? Guid.NewGuid();

    context.RefreshTokens.Add(new RefreshToken
    {
      UserId = userId,
      TokenHash = Hash(rawToken),
      Family = resolvedFamily,
      ExpiresAt = expiresAt,
      CreatedAt = DateTime.UtcNow
    });
    await context.SaveChangesAsync();

    return (rawToken, expiresAt, resolvedFamily);
  }

  private async Task RevokeFamilyAsync(BuoyAIDbContext context, Guid family)
  {
    var now = DateTime.UtcNow;
    var active = await context.RefreshTokens
      .Where(t => t.Family == family && t.RevokedAt == null)
      .ToListAsync();
    foreach (var token in active)
      token.RevokedAt = now;
    if (active.Count > 0)
      await context.SaveChangesAsync();
  }

  private static string GenerateRawToken()
  {
    var bytes = RandomNumberGenerator.GetBytes(32);
    // URL-safe base64 without padding — safe in cookies and JSON.
    return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
  }

  private static string Hash(string rawToken)
  {
    var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken));
    return Convert.ToHexString(bytes).ToLowerInvariant();
  }
}
