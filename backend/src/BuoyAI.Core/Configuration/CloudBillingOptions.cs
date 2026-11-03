namespace BuoyAI.Core.Configuration;

/// <summary>
/// Settings for talking to the BuoyAI Cloud billing service (a separate, private service that
/// owns the Stripe catalog and subscription changes). Bound to the "CloudBilling" section.
/// Off by default: with it off, the Cloud endpoints answer 404-style "not available" and no
/// call leaves the server, so hosted behaviour is unchanged until it is turned on.
/// </summary>
public class CloudBillingOptions
{
    public const string SectionName = "CloudBilling";

    public bool Enabled { get; set; }

    /// <summary>Base URL of the Cloud billing service, for example <c>http://cloud-billing:8080</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Shared secret sent as <c>X-Service-Key</c>. The service refuses calls without it.</summary>
    public string ServiceKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 20;
}
