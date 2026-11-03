namespace BuoyAI.Core.Entities;

/// <summary>
/// An OAuth 2.1 client registered with the BuoyAI Connect authorization server (RFC #782).
/// Two provenance kinds share this table:
///  - Pre-registered clients (config-declared <c>Mcp:Clients</c>, upserted at startup) —
///    fixed callback URLs for known agents (Claude, ChatGPT). <see cref="IsCimd"/> = false.
///  - CIMD clients: the <see cref="ClientId"/> is itself an https URL pointing at a Client ID
///    Metadata Document, fetched + cached here (<see cref="IsCimd"/> = true), refetched when the
///    cached copy is older than 24h.
/// All Connect clients are PUBLIC clients (token_endpoint_auth_method = none) and rely on PKCE.
/// </summary>
public class OAuthClient
{
  /// <summary>The client identifier (PK). For CIMD clients this is the https metadata-document URL.</summary>
  public string ClientId { get; set; } = string.Empty;

  public string ClientName { get; set; } = string.Empty;

  public string? ClientUri { get; set; }

  public string? LogoUri { get; set; }

  /// <summary>JSON array of the client's registered redirect URIs. Validated by exact string match.</summary>
  public string RedirectUris { get; set; } = "[]";

  /// <summary>True when this row was discovered/cached from a Client ID Metadata Document.</summary>
  public bool IsCimd { get; set; }

  /// <summary>When the CIMD was last fetched; drives the 24h refetch. Null for pre-registered clients.</summary>
  public DateTime? CimdFetchedAt { get; set; }

  public DateTime CreatedAt { get; set; }
}
