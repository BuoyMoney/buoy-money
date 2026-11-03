using Microsoft.Extensions.Options;
using BuoyAI.Core.Configuration;

namespace BuoyAI.API.Endpoints;

/// <summary>
/// Maps the OAuth 2.1 / RFC 8414 + RFC 9728 discovery documents for BuoyAI Connect (RFC #782).
/// These live OUTSIDE <c>/api</c> at <c>/.well-known/*</c>, so they are registered as minimal-API
/// endpoints here rather than on a controller. Program.cs calls this only when <c>Mcp:Enabled</c>.
/// All endpoints are anonymous. Documents are emitted as dictionaries so the snake_case keys survive
/// verbatim.
/// </summary>
public static class OAuthDiscoveryEndpoints
{
  public static IEndpointRouteBuilder MapOAuthDiscovery(this IEndpointRouteBuilder endpoints)
  {
    var options = endpoints.ServiceProvider.GetRequiredService<IOptions<McpOptions>>().Value;
    var issuer = (options.Issuer ?? string.Empty).TrimEnd('/');
    var resource = options.ResourceUri ?? string.Empty;

    IResult AuthorizationServerMetadata() => Results.Json(new Dictionary<string, object?>
    {
      ["issuer"] = issuer,
      ["authorization_endpoint"] = $"{issuer}/api/oauth/authorize",
      ["token_endpoint"] = $"{issuer}/api/oauth/token",
      ["scopes_supported"] = new[] { "buoyai:read", "buoyai:write:memory" },
      ["response_types_supported"] = new[] { "code" },
      ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
      ["code_challenge_methods_supported"] = new[] { "S256" },
      ["token_endpoint_auth_methods_supported"] = new[] { "none" },
      ["client_id_metadata_document_supported"] = true,
    });

    IResult ProtectedResourceMetadata() => Results.Json(new Dictionary<string, object?>
    {
      ["resource"] = resource,
      ["authorization_servers"] = new[] { issuer },
      ["scopes_supported"] = new[] { "buoyai:read", "buoyai:write:memory" },
      ["bearer_methods_supported"] = new[] { "header" },
    });

    endpoints.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata).AllowAnonymous();
    // OIDC-discovery alias — some clients probe this path for the authorization server metadata.
    endpoints.MapGet("/.well-known/openid-configuration", AuthorizationServerMetadata).AllowAnonymous();

    endpoints.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadata).AllowAnonymous();
    endpoints.MapGet("/.well-known/oauth-protected-resource/mcp", ProtectedResourceMetadata).AllowAnonymous();

    return endpoints;
  }
}
