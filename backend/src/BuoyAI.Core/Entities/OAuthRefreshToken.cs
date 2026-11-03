namespace BuoyAI.Core.Entities;

/// <summary>
/// A rotating refresh token issued to a BuoyAI Connect client (RFC #782). Deliberately SEPARATE
/// from the first-party <see cref="RefreshToken"/> table: Connect tokens have a different audience
/// (the MCP resource, never the first-party API), lifecycle, and revocation semantics, and must not
/// be interchangeable with a user's app session tokens.
///
/// Only the SHA-256 hash of the opaque 256-bit value is stored. Every use rotates the token: the
/// presented token is revoked and a replacement issued, linked via <see cref="ReplacedBy"/>.
/// Presenting an already-revoked token indicates replay/theft, so the whole rotation chain is
/// revoked (see OAuthConnectService).
/// </summary>
public class OAuthRefreshToken
{
  public Guid Id { get; set; }

  public int UserId { get; set; }

  public string ClientId { get; set; } = string.Empty;

  /// <summary>SHA-256 (hex) of the opaque refresh token. The raw value is never persisted.</summary>
  public string TokenHash { get; set; } = string.Empty;

  public string Scope { get; set; } = string.Empty;

  public string? Resource { get; set; }

  public DateTime ExpiresAt { get; set; }

  public DateTime CreatedAt { get; set; }

  public DateTime? RevokedAt { get; set; }

  /// <summary>The token that replaced this one on rotation, if any.</summary>
  public Guid? ReplacedBy { get; set; }

  public bool IsActive => RevokedAt == null && DateTime.UtcNow < ExpiresAt;
}
