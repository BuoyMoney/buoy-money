using BuoyAI.Core.DTOs.OAuth;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Core of the BuoyAI Connect OAuth 2.1 authorization server (RFC #782). Owns client resolution
/// (pre-registered + CIMD), the authorize-request lifecycle, PKCE verification, single-use
/// authorization codes, access-token minting, and rotating refresh tokens. The controller is a thin
/// HTTP adapter over this service.
/// </summary>
public interface IOAuthConnectService
{
  /// <summary>
  /// Validates an authorize request, resolves the client (pre-registered or CIMD), and either
  /// creates a pending authorization request (→ consent redirect) or returns an error outcome.
  /// </summary>
  Task<OAuthAuthorizeResult> HandleAuthorizeAsync(OAuthAuthorizeInput input, CancellationToken ct);

  /// <summary>
  /// Returns the consent-screen view for a pending request, or null if it is expired, consumed,
  /// or already decided (→ 404).
  /// </summary>
  Task<OAuthConsentView?> GetConsentViewAsync(Guid requestId, CancellationToken ct);

  /// <summary>
  /// Records the authenticated user's approve/deny decision, minting a single-use code on approve.
  /// Returns the redirect URL, or null if the request is expired/consumed/already-decided (→ 404).
  /// </summary>
  Task<OAuthDecisionResult?> DecideAsync(Guid requestId, int userId, bool approve, CancellationToken ct);

  /// <summary>Exchanges an authorization code or refresh token for a new token set.</summary>
  Task<OAuthTokenResult> ExchangeTokenAsync(OAuthTokenInput input, CancellationToken ct);

  /// <summary>
  /// Idempotently upserts the config-declared pre-registered clients (<c>Mcp:Clients</c>) into
  /// oauth_clients. Called once at startup when the feature flag is on.
  /// </summary>
  Task UpsertConfiguredClientsAsync(CancellationToken ct);
}
