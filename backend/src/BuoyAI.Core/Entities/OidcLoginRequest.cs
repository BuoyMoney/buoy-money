namespace BuoyAI.Core.Entities;

/// <summary>
/// A short-lived step of a single sign-on (OpenID Connect) round trip on a self-hosted server.
/// <list type="bullet">
/// <item><c>state</c> rows live from "start" until the provider sends the browser back (10 minutes at
/// most). They hold the nonce and the PKCE verifier for the provider.</item>
/// <item><c>exchange</c> rows hold the one-time code the app trades for its tokens (60 seconds, single
/// use), so tokens never travel in a URL.</item>
/// </list>
/// Only a SHA-256 hash of the state or exchange code is stored. A row is deleted when it is used,
/// which is what makes each one single use.
/// </summary>
public class OidcLoginRequest
{
  public const string KindState = "state";
  public const string KindExchange = "exchange";

  public Guid Id { get; set; }

  /// <summary><see cref="KindState"/> or <see cref="KindExchange"/>.</summary>
  public string Kind { get; set; } = KindState;

  /// <summary>Hex SHA-256 of the state value (state rows) or of the exchange code (exchange rows).</summary>
  public string SecretHash { get; set; } = string.Empty;

  /// <summary>"web" or "mobile": where the browser goes back to after the provider.</summary>
  public string Platform { get; set; } = "web";

  /// <summary>The nonce sent to the provider; the ID token must echo it. State rows only.</summary>
  public string? Nonce { get; set; }

  /// <summary>PKCE verifier for the provider's token endpoint. State rows only.</summary>
  public string? CodeVerifier { get; set; }

  /// <summary>An in-app path to open after sign-in (web), checked to be a local path.</summary>
  public string? ReturnPath { get; set; }

  /// <summary>An invite code the person brought from the sign-up page.</summary>
  public string? InviteCode { get; set; }

  /// <summary>
  /// The app's own PKCE challenge (S256). The exchange must present the matching verifier, so a
  /// different app that grabs the deep link cannot use the code. Required for mobile.
  /// </summary>
  public string? ClientChallenge { get; set; }

  /// <summary>The signed-in user. Exchange rows only.</summary>
  public int? UserId { get; set; }

  /// <summary>True when the callback created the account. Exchange rows only.</summary>
  public bool IsNewUser { get; set; }

  public DateTime CreatedAt { get; set; }

  public DateTime ExpiresAt { get; set; }
}
