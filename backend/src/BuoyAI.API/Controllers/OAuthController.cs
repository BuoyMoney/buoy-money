using BuoyAI.API.Filters;
using BuoyAI.Core.DTOs.OAuth;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// BuoyAI Connect OAuth 2.1 authorization-server endpoints (RFC #782), rooted at <c>/api/oauth</c>.
/// authorize + token are anonymous; the two consent endpoints require the first-party JWT. The
/// whole controller 404s when the <c>Mcp:Enabled</c> flag is off.
/// </summary>
[Route("api/oauth")]
[RequireMcpEnabled]
public class OAuthController : BaseController
{
  private readonly IOAuthConnectService _connectService;
  private readonly ILogger<OAuthController> _logger;

  public OAuthController(IOAuthConnectService connectService, ILogger<OAuthController> logger)
  {
    _connectService = connectService;
    _logger = logger;
  }

  /// <summary>OAuth 2.1 authorization endpoint. Validates the request, resolves the client
  /// (pre-registered or CIMD), and either redirects to the first-party consent screen or returns an
  /// error (redirect back to the client for recoverable errors; an HTML page for an invalid
  /// client/redirect_uri).</summary>
  [HttpGet("authorize")]
  [AllowAnonymous]
  public async Task<IActionResult> Authorize(
    [FromQuery(Name = "response_type")] string? responseType,
    [FromQuery(Name = "client_id")] string? clientId,
    [FromQuery(Name = "redirect_uri")] string? redirectUri,
    [FromQuery(Name = "scope")] string? scope,
    [FromQuery(Name = "state")] string? state,
    [FromQuery(Name = "code_challenge")] string? codeChallenge,
    [FromQuery(Name = "code_challenge_method")] string? codeChallengeMethod,
    [FromQuery(Name = "resource")] string? resource,
    CancellationToken ct)
  {
    var input = new OAuthAuthorizeInput
    {
      ResponseType = responseType,
      ClientId = clientId,
      RedirectUri = redirectUri,
      Scope = scope,
      State = state,
      CodeChallenge = codeChallenge,
      CodeChallengeMethod = codeChallengeMethod,
      Resource = resource,
    };

    var result = await _connectService.HandleAuthorizeAsync(input, ct);
    return result.Kind switch
    {
      AuthorizeOutcomeKind.RedirectToConsent => Redirect(result.RedirectUrl!),
      AuthorizeOutcomeKind.RedirectWithError => Redirect(result.RedirectUrl!),
      _ => ErrorPage(result),
    };
  }

  /// <summary>Consent-screen data for a pending authorization request. First-party JWT required.</summary>
  [HttpGet("request/{id:guid}")]
  [Authorize]
  public async Task<IActionResult> GetRequest(Guid id, CancellationToken ct)
  {
    var view = await _connectService.GetConsentViewAsync(id, ct);
    return HandleServiceResponse(view, "This connection request has expired or was already handled.");
  }

  /// <summary>Records the user's approve/deny decision and returns the client redirect URL.</summary>
  [HttpPost("request/{id:guid}/decision")]
  [Authorize]
  public async Task<IActionResult> Decide(Guid id, [FromBody] OAuthDecisionRequest body, CancellationToken ct)
  {
    var userId = GetCurrentUserId();
    var result = await _connectService.DecideAsync(id, userId, body?.Approve ?? false, ct);
    return HandleServiceResponse(result, "This connection request has expired or was already handled.");
  }

  /// <summary>OAuth 2.1 token endpoint. Accepts ONLY application/x-www-form-urlencoded. Anonymous
  /// (public clients authenticate via PKCE, not a client secret).</summary>
  [HttpPost("token")]
  [AllowAnonymous]
  [Consumes("application/x-www-form-urlencoded")]
  public async Task<IActionResult> Token(CancellationToken ct)
  {
    var form = await Request.ReadFormAsync(ct);
    var input = new OAuthTokenInput
    {
      GrantType = form["grant_type"],
      Code = form["code"],
      RedirectUri = form["redirect_uri"],
      ClientId = form["client_id"],
      CodeVerifier = form["code_verifier"],
      RefreshToken = form["refresh_token"],
      Resource = form["resource"],
    };

    var result = await _connectService.ExchangeTokenAsync(input, ct);
    if (result.Success)
      return Ok(result.Token);

    // RFC 6749 §5.2 error body. Bearer errors use 400; client-auth failures 401.
    Response.StatusCode = result.StatusCode;
    return new JsonResult(new OAuthErrorResponse
    {
      Error = result.Error!,
      ErrorDescription = result.ErrorDescription,
    });
  }

  private ContentResult ErrorPage(OAuthAuthorizeResult result)
  {
    var title = System.Net.WebUtility.HtmlEncode(result.ErrorTitle ?? "Authorization error");
    var message = System.Net.WebUtility.HtmlEncode(result.ErrorMessage ?? "The request could not be processed.");
    var html =
      "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
      "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
      $"<title>{title}</title></head><body style=\"font-family:system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;\">" +
      $"<h1 style=\"font-size:1.25rem;\">{title}</h1><p>{message}</p></body></html>";
    return new ContentResult
    {
      StatusCode = result.ErrorStatusCode,
      ContentType = "text/html; charset=utf-8",
      Content = html,
    };
  }
}

/// <summary>Body of <c>POST /api/oauth/request/{id}/decision</c>.</summary>
public class OAuthDecisionRequest
{
  public bool Approve { get; set; }
}

/// <summary>RFC 6749 §5.2 error body; snake_case pinned via JsonPropertyName.</summary>
public class OAuthErrorResponse
{
  [System.Text.Json.Serialization.JsonPropertyName("error")]
  public string Error { get; set; } = string.Empty;

  [System.Text.Json.Serialization.JsonPropertyName("error_description")]
  public string? ErrorDescription { get; set; }
}
