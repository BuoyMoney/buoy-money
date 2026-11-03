namespace BuoyAI.Core.Configuration;

/// <summary>
/// Binds the <c>Mcp</c> configuration section (RFC #782). The feature flag <see cref="Enabled"/>
/// gates the entire BuoyAI Connect surface: when false the OAuth + discovery endpoints 404 and the
/// MCP endpoint is never mapped. <see cref="ResourceUri"/> is the pinned token audience and
/// <see cref="Issuer"/> the public origin / token issuer.
/// </summary>
public class McpOptions
{
  public const string SectionName = "Mcp";

  public bool Enabled { get; set; }

  /// <summary>Canonical MCP resource URI — the audience every access token is pinned to.</summary>
  public string? ResourceUri { get; set; }

  /// <summary>Token issuer and public origin (e.g. https://staging.buoy-ai.com).</summary>
  public string? Issuer { get; set; }

  /// <summary>Config-declared pre-registered clients, upserted into oauth_clients at startup.</summary>
  public List<McpClientConfig> Clients { get; set; } = new();
}

/// <summary>
/// A pre-registered OAuth client declared in configuration (<c>Mcp:Clients</c>). Upserted into
/// oauth_clients on startup so known agents (Claude, ChatGPT) get their fixed callback URLs
/// registered without direct DB writes.
/// </summary>
public class McpClientConfig
{
  public string ClientId { get; set; } = string.Empty;
  public string ClientName { get; set; } = string.Empty;
  public string? ClientUri { get; set; }
  public string? LogoUri { get; set; }
  public List<string> RedirectUris { get; set; } = new();
}
