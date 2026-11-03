using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs;

/// <summary>How the client should launch a provider's connection UI.</summary>
public enum AggregatorConnectKind
{
    /// <summary>An opaque token handed to a native SDK (e.g. Plaid Link's link_token).</summary>
    LinkToken,

    /// <summary>A hosted widget URL the client embeds/opens (e.g. MX's Connect Widget URL).</summary>
    WidgetUrl,

    /// <summary>A full-page redirect/OAuth URL the client navigates to.</summary>
    RedirectUrl,

    /// <summary>
    /// The user makes a token elsewhere and pastes it into BuoyAI (SimpleFIN setup token).
    /// <c>Value</c> is the page where the token is created.
    /// </summary>
    SetupTokenPrompt,
}

/// <summary>
/// Provider-neutral "how to start connecting a bank" payload. Each aggregator maps its own
/// launch artifact onto this so the client can drive any provider through one flow.
/// </summary>
public class AggregatorConnectStart
{
    public BankProvider Provider { get; set; }
    public AggregatorConnectKind Kind { get; set; }

    /// <summary>The link token / widget URL / redirect URL the client launches.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>When <see cref="Value"/> stops being usable, if the provider says (e.g. MX widget URLs expire ~10 min).</summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Provider-neutral result of completing a connection from the provider's post-connect callback
/// artifact (Plaid public_token, MX member_guid, …). The plaintext provider access token is never
/// surfaced — it is persisted encrypted server-side.
/// </summary>
public class AggregatorConnectResult
{
    public BankProvider Provider { get; set; }
    public bool Success { get; set; }

    /// <summary>The provider's connection identifier (Plaid item_id / MX member_guid), for correlation only.</summary>
    public string? ExternalConnectionId { get; set; }

    public string? Message { get; set; }
}

/// <summary>One bank connection as the provider-neutral list shows it.</summary>
public class BankConnectionSummaryDto
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    /// <summary>The connection is waiting on the user (sign in again, or paste a new token).</summary>
    public bool RequiresUserAction { get; set; }

    /// <summary>Active, LoginRequired, Error, Disconnected and so on.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The last time a sync finished cleanly, if one has.</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>Why the connection needs attention, in words safe to show. Null when it does not.</summary>
    public string? Message { get; set; }

    public int AccountCount { get; set; }
}
