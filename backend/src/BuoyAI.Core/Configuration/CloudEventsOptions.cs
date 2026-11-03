namespace BuoyAI.Core.Configuration;

/// <summary>
/// Settings for the receiver of BuoyAI Cloud events (section <c>CloudEvents</c>): the AI gateway's 80 and 100 percent
/// allowance alerts, and the bank bridge's "new server paired" and "bank needs attention" notices. The Cloud services
/// do not send email or push themselves; they post a signed event here and this app tells the customer.
/// Off unless <see cref="WebhookSecret"/> is set (at least <see cref="MinSecretLength"/> characters).
/// </summary>
public class CloudEventsOptions
{
    public const string SectionName = "CloudEvents";
    public const int MinSecretLength = 32;

    /// <summary>
    /// Shared HMAC-SHA256 secret. Every Cloud service that posts here signs the raw body with it and sends
    /// <c>X-Buoy-Signature: sha256=&lt;hex&gt;</c>. Empty or too short: the receiver answers 404 and does nothing.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsEnabled => (WebhookSecret ?? string.Empty).Length >= MinSecretLength;
}
