namespace BuoyAI.Core.Interfaces;

/// <summary>A newly issued refresh token: the raw value goes to the client (cookie
/// or body); only its hash is stored server-side.</summary>
public record IssuedRefreshToken(string Token, DateTime ExpiresAt, Guid Family);

/// <summary>The result of rotating a refresh token — the validated user plus the
/// replacement token to hand back to the client.</summary>
public record RotatedRefreshToken(int UserId, string Token, DateTime ExpiresAt);

/// <summary>
/// Issues, rotates, and revokes refresh tokens with reuse detection. Backs the
/// session model that replaces the bare 24h JWT: a short-lived access token plus a
/// rotating, revocable refresh token.
/// </summary>
public interface IRefreshTokenService
{
  /// <summary>Issues a new refresh token for the user, starting a new rotation family
  /// (or continuing <paramref name="family"/> when rotating).</summary>
  Task<IssuedRefreshToken> IssueAsync(int userId, Guid? family = null);

  /// <summary>
  /// Validates and rotates the presented refresh token. Returns the new token on
  /// success, or <c>null</c> if the token is unknown, expired, or revoked. Presenting
  /// an already-revoked token is treated as reuse and revokes the entire family.
  /// </summary>
  Task<RotatedRefreshToken?> RotateAsync(string rawToken);

  /// <summary>Revokes the presented refresh token (logout). No-op if unknown.</summary>
  Task RevokeAsync(string rawToken);

  /// <summary>
  /// Revokes every active refresh token for the user — ending all of their sessions on
  /// every device. Backs "sign out everywhere" / trusted-device revocation: after this,
  /// each device's next rotation fails and it is forced to re-authenticate. Returns the
  /// number of tokens revoked.
  /// </summary>
  Task<int> RevokeAllForUserAsync(int userId);
}
