namespace BuoyAI.Core.Entities;

/// <summary>
/// A server-side refresh token, stored as a SHA-256 hash of the opaque value the
/// client holds (the raw token is never persisted). Refresh tokens rotate on every
/// use: the presented token is revoked and a replacement is issued in the same
/// <see cref="Family"/>. Presenting an already-revoked token indicates replay/theft,
/// so the whole family is revoked (see RefreshTokenService). This gives the session
/// revocation and short access-token lifetimes the bare 24h JWT lacked.
/// </summary>
public class RefreshToken
{
  public int Id { get; set; }
  public int UserId { get; set; }

  /// <summary>SHA-256 (hex) of the opaque refresh token. Unique.</summary>
  public string TokenHash { get; set; } = string.Empty;

  /// <summary>Rotation lineage. All descendants of one login share a family, so a
  /// detected reuse can revoke the entire chain.</summary>
  public Guid Family { get; set; }

  public DateTime ExpiresAt { get; set; }
  public DateTime CreatedAt { get; set; }
  public DateTime? RevokedAt { get; set; }

  /// <summary>Hash of the token that replaced this one on rotation, if any.</summary>
  public string? ReplacedByTokenHash { get; set; }

  public User User { get; set; } = null!;

  public bool IsActive => RevokedAt == null && DateTime.UtcNow < ExpiresAt;
}
