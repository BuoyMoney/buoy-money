namespace BuoyAI.Core.DTOs.Auth;

/// <summary>Who the single sign-on provider says signed in, after the ID token was verified.</summary>
public sealed record OidcIdentity(
  string Issuer,
  string Subject,
  string? Email,
  bool EmailVerified,
  string? GivenName,
  string? FamilyName,
  string? Name);

/// <summary>The account a verified single sign-on identity resolved to.</summary>
public sealed record OidcResolvedUser(int UserId, bool IsNewUser);

/// <summary>Body of <c>POST /api/auth/oidc/exchange</c>.</summary>
public class OidcExchangeRequest
{
  /// <summary>The one-time code from the redirect back to the app.</summary>
  public string Code { get; set; } = string.Empty;

  /// <summary>The app's PKCE verifier, when it sent a challenge to start (always on mobile).</summary>
  public string? CodeVerifier { get; set; }

  public string? DeviceFingerprint { get; set; }

  public bool RememberDevice { get; set; }
}
