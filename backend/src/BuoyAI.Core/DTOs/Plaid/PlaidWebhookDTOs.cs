using System.Text.Json.Serialization;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.Plaid;

// Webhook DTOs
// Plaid sends webhook payloads in snake_case; the API's global JSON policy is camelCase, so the
// inbound field names are pinned explicitly with [JsonPropertyName] to guarantee they bind.
public class PlaidWebhookRequest
{
    [JsonPropertyName("webhook_type")]
    public string WebhookType { get; set; } = null!;

    [JsonPropertyName("webhook_code")]
    public string WebhookCode { get; set; } = null!;

    [JsonPropertyName("item_id")]
    public string ItemId { get; set; } = null!;

    [JsonPropertyName("error")]
    public PlaidError? Error { get; set; }

    // Transaction IDs Plaid removed at the source (TRANSACTIONS_REMOVED webhook), e.g. a
    // cancelled pending charge or a reversed posted transaction. Must be purged locally or they
    // permanently skew spend totals / budgets / insights.
    [JsonPropertyName("removed_transactions")]
    public List<string>? RemovedTransactions { get; set; }

    [JsonPropertyName("additional_data")]
    public Dictionary<string, object>? AdditionalData { get; set; }
}

public class PlaidError
{
    [JsonPropertyName("error_type")]
    public string ErrorType { get; set; } = null!;

    [JsonPropertyName("error_code")]
    public string ErrorCode { get; set; } = null!;

    [JsonPropertyName("error_message")]
    public string ErrorMessage { get; set; } = null!;

    [JsonPropertyName("display_message")]
    public string? DisplayMessage { get; set; }
}

// Update Mode DTOs
public class CreateUpdateTokenRequest
{
    public int ConnectionId { get; set; }

    /// <summary>Open Link update mode with account selection (NEW_ACCOUNTS_AVAILABLE flow).</summary>
    public bool AccountSelectionEnabled { get; set; } = false;

    /// <summary>"android" / "ios" / "web": selects the OAuth return config, as on fresh connects.</summary>
    public string? Platform { get; set; }
}

public class CreateUpdateTokenResponse
{
    public string LinkToken { get; set; } = null!;
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
}

public class UpdateConnectionRequest
{
    public int ConnectionId { get; set; }
    public string PublicToken { get; set; } = null!;
}

public class ConnectionStatusResponse
{
    public int ConnectionId { get; set; }
    public string InstitutionName { get; set; } = null!;
    public PlaidItemStatus Status { get; set; }
    public string? LastError { get; set; }
    public DateTime? ExpirationDate { get; set; }
    public DateTime? DisconnectDate { get; set; }
    public bool RequiresUserAction { get; set; }
    public DateTime? LastStatusUpdate { get; set; }
}
