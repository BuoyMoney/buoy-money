namespace BuoyAI.Core.Entities;

/// <summary>
/// A long-lived access token a self-hosted user creates for a tool that cannot do the BuoyAI Connect
/// OAuth flow (Open WebUI, a script). The token itself is a Connect JWT (MCP issuer and audience,
/// client_id <c>personal-token</c>), so it is only ever valid on <c>/mcp</c>. This row is what makes
/// it revocable: the JWT's <c>jti</c> must match an unrevoked row on every call. The token value is
/// shown once at creation and never stored.
/// </summary>
public class ConnectPersonalToken
{
  public Guid Id { get; set; }

  public int UserId { get; set; }

  /// <summary>What the user called it, e.g. "Open WebUI".</summary>
  public string Name { get; set; } = string.Empty;

  public DateTime CreatedAt { get; set; }

  public DateTime ExpiresAt { get; set; }

  public DateTime? LastUsedAt { get; set; }

  public DateTime? RevokedAt { get; set; }

  public bool IsActive => RevokedAt == null && DateTime.UtcNow < ExpiresAt;
}
