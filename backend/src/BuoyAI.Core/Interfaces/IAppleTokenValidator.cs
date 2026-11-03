using BuoyAI.Core.DTOs.Auth;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Verifies Apple "Sign in with Apple" identity tokens. Wrapping the JWKS
/// signature/issuer/audience validation behind an interface keeps
/// <c>AuthService</c> testable and ensures the Apple login path can never fall
/// back to trusting an unverified token.
/// </summary>
public interface IAppleTokenValidator
{
  /// <summary>
  /// Validates the supplied Apple identity token — signature (against Apple's
  /// JWKS), issuer, audience, and expiry — and returns its payload, or
  /// <c>null</c> if the token is invalid or verification is not configured.
  /// </summary>
  /// <param name="identityToken">The raw Apple JWT from the client.</param>
  /// <param name="expectedNonce">
  /// The nonce the client bound to the sign-in request, if any. When supplied it
  /// is checked against the token's <c>nonce</c> claim to defend against replay.
  /// </param>
  Task<AppleTokenPayload?> ValidateAsync(string identityToken, string? expectedNonce = null);

  /// <summary>
  /// Verifies an Apple server-to-server notification's signed <c>payload</c> JWT
  /// (signature against Apple's JWKS + issuer + audience) and returns the parsed
  /// event, or <c>null</c> if verification fails. Notifications carry no nonce and
  /// need not carry an expiry, so lifetime is not enforced.
  /// </summary>
  Task<AppleServerNotification?> ValidateServerNotificationAsync(string signedPayload);
}
