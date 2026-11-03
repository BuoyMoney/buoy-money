using System.Text.Json.Serialization;

namespace BuoyAI.Core.DTOs.OAuth;

/// <summary>Parsed query parameters for <c>GET /api/oauth/authorize</c>.</summary>
public class OAuthAuthorizeInput
{
  public string? ResponseType { get; set; }
  public string? ClientId { get; set; }
  public string? RedirectUri { get; set; }
  public string? Scope { get; set; }
  public string? State { get; set; }
  public string? CodeChallenge { get; set; }
  public string? CodeChallengeMethod { get; set; }
  public string? Resource { get; set; }
}

/// <summary>How the controller should respond to an authorize request.</summary>
public enum AuthorizeOutcomeKind
{
  /// <summary>Client + redirect_uri valid → 302 to the first-party consent screen.</summary>
  RedirectToConsent,

  /// <summary>Recoverable error with a validated redirect_uri → 302 back to the client with error+state.</summary>
  RedirectWithError,

  /// <summary>Invalid client/redirect_uri → render an HTML error page; never redirect.</summary>
  ErrorPage,
}

/// <summary>Discriminated result of an authorize request.</summary>
public class OAuthAuthorizeResult
{
  public AuthorizeOutcomeKind Kind { get; init; }
  public string? RedirectUrl { get; init; }
  public int ErrorStatusCode { get; init; }
  public string? ErrorTitle { get; init; }
  public string? ErrorMessage { get; init; }

  public static OAuthAuthorizeResult Consent(string url) =>
    new() { Kind = AuthorizeOutcomeKind.RedirectToConsent, RedirectUrl = url };

  public static OAuthAuthorizeResult RedirectError(string url) =>
    new() { Kind = AuthorizeOutcomeKind.RedirectWithError, RedirectUrl = url };

  public static OAuthAuthorizeResult Error(int status, string title, string message) =>
    new() { Kind = AuthorizeOutcomeKind.ErrorPage, ErrorStatusCode = status, ErrorTitle = title, ErrorMessage = message };
}

/// <summary>Consent-screen view for <c>GET /api/oauth/request/{id}</c>.</summary>
public class OAuthConsentView
{
  public string ClientName { get; set; } = string.Empty;
  public string? ClientUri { get; set; }
  public string? LogoUri { get; set; }
  public string[] Scopes { get; set; } = Array.Empty<string>();
  public string[] ScopeDescriptions { get; set; } = Array.Empty<string>();
  public DateTime ExpiresAt { get; set; }
}

/// <summary>Result of a consent decision (<c>POST /api/oauth/request/{id}/decision</c>).</summary>
public class OAuthDecisionResult
{
  public string RedirectUrl { get; set; } = string.Empty;
}

/// <summary>Parsed form body for <c>POST /api/oauth/token</c>.</summary>
public class OAuthTokenInput
{
  public string? GrantType { get; set; }
  public string? Code { get; set; }
  public string? RedirectUri { get; set; }
  public string? ClientId { get; set; }
  public string? CodeVerifier { get; set; }
  public string? RefreshToken { get; set; }
  public string? Resource { get; set; }
}

/// <summary>RFC 6749 token-endpoint success body. snake_case is pinned via JsonPropertyName so it
/// survives the API's global camelCase policy.</summary>
public class OAuthTokenResponse
{
  [JsonPropertyName("access_token")]
  public string AccessToken { get; set; } = string.Empty;

  [JsonPropertyName("token_type")]
  public string TokenType { get; set; } = "Bearer";

  [JsonPropertyName("expires_in")]
  public int ExpiresIn { get; set; } = 3600;

  [JsonPropertyName("refresh_token")]
  public string RefreshToken { get; set; } = string.Empty;

  [JsonPropertyName("scope")]
  public string Scope { get; set; } = string.Empty;
}

/// <summary>Discriminated result of a token exchange. On failure carries the RFC 6749 error code.</summary>
public class OAuthTokenResult
{
  public bool Success { get; init; }
  public int StatusCode { get; init; }
  public OAuthTokenResponse? Token { get; init; }
  public string? Error { get; init; }
  public string? ErrorDescription { get; init; }

  public static OAuthTokenResult Ok(OAuthTokenResponse token) =>
    new() { Success = true, StatusCode = 200, Token = token };

  public static OAuthTokenResult Fail(int status, string error, string? description = null) =>
    new() { Success = false, StatusCode = status, Error = error, ErrorDescription = description };
}

/// <summary>A personal access token as listed in settings. The token value is never shown again.</summary>
public class ConnectPersonalTokenDto
{
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; }
  public DateTime ExpiresAt { get; set; }
  public DateTime? LastUsedAt { get; set; }
}

/// <summary>A newly created personal access token, with its value (shown once) and the address to use it at.</summary>
public class CreatedConnectPersonalTokenDto : ConnectPersonalTokenDto
{
  public string Token { get; set; } = string.Empty;
  public string McpUrl { get; set; } = string.Empty;
}

/// <summary>Body of POST /api/connect/tokens.</summary>
public class CreateConnectPersonalTokenRequest
{
  public string? Name { get; set; }
}
