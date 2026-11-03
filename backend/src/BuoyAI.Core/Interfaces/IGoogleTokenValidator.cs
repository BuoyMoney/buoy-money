using BuoyAI.Core.DTOs.Auth;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Verifies Google ID tokens. Wrapping the Google SDK's static validation behind
/// an interface keeps <c>AuthService</c> testable — tests can supply a fake token
/// payload without a real signed Google JWT.
/// </summary>
public interface IGoogleTokenValidator
{
  /// <summary>
  /// Validates the supplied Google ID token and returns its payload, or
  /// <c>null</c> if the token is invalid.
  /// </summary>
  Task<GoogleTokenPayload?> ValidateAsync(string idToken);
}
