namespace BuoyAI.Core.DTOs.Cloud;

/// <summary>
/// One event posted by a BuoyAI Cloud service (JSON, camelCase). <see cref="Type"/> decides which fields matter:
/// <list type="bullet">
/// <item><c>ai_allowance</c> (AI gateway): level, percent, usedMicros, allowanceMicros, paygEnabled, periodEnd.</item>
/// <item><c>server_paired</c> (bridge): serverId, serverName, replacedKeys.</item>
/// <item><c>bank_needs_attention</c> (bridge): itemId, reason.</item>
/// </list>
/// <see cref="AccountId"/> is the Cloud account id, which is the customer's Stripe customer id.
/// </summary>
public class CloudEvent
{
    public string? Type { get; set; }
    public string? AccountId { get; set; }

    // ai_allowance
    public int? Level { get; set; }
    public int? Percent { get; set; }
    public long? UsedMicros { get; set; }
    public long? AllowanceMicros { get; set; }
    public bool? PaygEnabled { get; set; }
    public DateTime? PeriodEnd { get; set; }

    // server_paired
    public Guid? ServerId { get; set; }
    public string? ServerName { get; set; }
    public bool? ReplacedKeys { get; set; }

    // bank_needs_attention
    public Guid? ItemId { get; set; }
    public string? Reason { get; set; }
}

/// <summary>What the receiver did with an event. Every outcome is still a 200 to the sender, so it does not retry forever.</summary>
public enum CloudEventOutcome
{
    Delivered,
    UnknownType,
    UnknownAccount,
    Invalid,
}
