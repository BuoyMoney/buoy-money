namespace BuoyAI.Core.Entities;

/// <summary>
/// One in-flight OAuth 2.1 authorization-code request (RFC #782). Created by
/// <c>GET /api/oauth/authorize</c> (10-minute TTL), bound to a user + decided at the first-party
/// consent screen, then exchanged once at the token endpoint. Carries the single-use authorization
/// <see cref="Code"/> (60s TTL) that the token endpoint consumes.
/// </summary>
public class OAuthAuthorizationRequest
{
  public Guid Id { get; set; }

  public string ClientId { get; set; } = string.Empty;

  /// <summary>Bound at consent time from the authenticated first-party principal. Null until decided.</summary>
  public int? UserId { get; set; }

  public string RedirectUri { get; set; } = string.Empty;

  public string Scope { get; set; } = string.Empty;

  /// <summary>Opaque client CSRF value; passed back untouched on the redirect.</summary>
  public string? State { get; set; }

  public string CodeChallenge { get; set; } = string.Empty;

  public string CodeChallengeMethod { get; set; } = "S256";

  /// <summary>RFC 8707 resource indicator; when present must equal the configured Mcp:ResourceUri.</summary>
  public string? Resource { get; set; }

  /// <summary>Single-use authorization code (crypto-random). Null until the request is approved.</summary>
  public string? Code { get; set; }

  public DateTime? CodeExpiresAt { get; set; }

  /// <summary>Set once the code has been redeemed. A second redemption is treated as replay.</summary>
  public bool Consumed { get; set; }

  /// <summary>Null = undecided, true = approved, false = denied.</summary>
  public bool? Approved { get; set; }

  public DateTime CreatedAt { get; set; }

  public DateTime ExpiresAt { get; set; }
}
