namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Client for Apple's "Sign in with Apple" server endpoints:
/// generating the ES256 client secret (a JWT signed with the team's <c>.p8</c> key),
/// exchanging a login authorization code for tokens (to capture a refresh token), and
/// revoking a token when a user deletes their account (Apple's account-deletion
/// requirement). All operations no-op gracefully when <c>AppleAuth</c> config
/// (TeamId / KeyId / ClientId / PrivateKey) is absent, so auth + deletion never break
/// on an unconfigured deploy.
/// </summary>
public interface IAppleAuthClient
{
    /// <summary>True when TeamId, KeyId, ClientId, and the private key are all present.</summary>
    bool IsConfigured { get; }

    /// <summary>Builds the short-lived ES256 client-secret JWT used with Apple's token/revoke endpoints.</summary>
    string GenerateClientSecret();

    /// <summary>
    /// Exchanges the login authorization code for tokens and returns the refresh token
    /// (or null on failure / when unconfigured). Store it encrypted to enable later revocation.
    /// </summary>
    Task<string?> ExchangeAuthorizationCodeAsync(string authorizationCode, CancellationToken cancellationToken = default);

    /// <summary>Revokes an Apple token (default a refresh token). Returns true on success.</summary>
    Task<bool> RevokeAsync(string token, string tokenTypeHint = "refresh_token", CancellationToken cancellationToken = default);
}
