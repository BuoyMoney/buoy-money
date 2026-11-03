using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// The app's view of the BuoyAI Cloud billing service. The app NEVER references a Cloud project;
/// this interface is the whole seam, and its one implementation speaks HTTP. Cloud is private and
/// is not part of the open-source export.
/// </summary>
public interface ICloudBillingClient
{
    Task<CloudCatalog> GetCatalogAsync(bool founding, CancellationToken ct = default);
    Task<CloudCheckoutResponse> CheckoutAsync(CloudCheckoutRequest request, CancellationToken ct = default);

    /// <summary>The customer's Cloud subscription, or null when they have none.</summary>
    Task<CloudSubscriptionState?> GetSubscriptionAsync(string customerId, string environment, CancellationToken ct = default);

    Task<CloudPreview> PreviewAsync(CloudChangeRequest request, CancellationToken ct = default);
    Task<CloudSubscriptionState> ApplyAsync(CloudChangeRequest request, CancellationToken ct = default);

    /// <summary>The invoice the subscription will produce next, line by line, or null when the customer has no Cloud plan.</summary>
    Task<CloudUpcomingInvoice?> GetUpcomingAsync(string customerId, string environment, CancellationToken ct = default);

    /// <summary>Opens a Stripe customer portal session (card, billing details, invoices). Returns the page's address.</summary>
    Task<CloudPortalSession> CreatePortalSessionAsync(CloudPortalSessionRequest request, CancellationToken ct = default);
}

/// <summary>
/// The Cloud service refused, with a stable <see cref="Code"/> (for example <c>already_owned</c>,
/// <c>payment_required</c>, <c>invalid_selection</c>) and a message safe to show.
/// </summary>
public class CloudBillingException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }

    public CloudBillingException(string code, string message, int statusCode) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }
}

/// <summary>Per-user Cloud billing operations, built on <see cref="ICloudBillingClient"/>.</summary>
public interface ICloudBillingService
{
    bool IsEnabled { get; }
    Task<CloudBillingStatus> GetStatusAsync(int userId, CancellationToken ct = default);
    Task<CloudCatalog> GetCatalogAsync(int userId, CancellationToken ct = default);
    /// <param name="platform">
    /// "web", "android", "ios" (refused) or "portal": a web purchase started on the Cloud portal, which returns to
    /// buoy-ai.com/cloud/billing instead of the hosted app.
    /// </param>
    Task<string> StartCheckoutAsync(
        int userId, string interval, IReadOnlyList<CloudProductSelection> products,
        string? platform, string? idempotencyKey, CancellationToken ct = default);
    Task<CloudPreview> PreviewAsync(
        int userId, string type, string? product, int? quantity, DateTime? prorationDate, CancellationToken ct = default);
    Task<CloudSubscriptionState> ApplyAsync(
        int userId, string type, string? product, int? quantity, DateTime? prorationDate,
        string? idempotencyKey, CancellationToken ct = default);

    /// <summary>The plan and the next invoice for the billing screen. Never throws for a billing outage: it says <c>LookupFailed</c>.</summary>
    Task<CloudBillingSummary> GetSummaryAsync(int userId, CancellationToken ct = default);

    /// <summary>A Stripe customer portal address for the user's own Cloud customer. Stripe sends them back to the app's Cloud billing screen.</summary>
    Task<string> CreatePortalSessionAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Same, but Stripe sends them back to the Cloud portal (buoy-ai.com/cloud/billing) when
    /// <paramref name="fromPortal"/> is true, so a Cloud-only customer never lands in the hosted app.
    /// </summary>
    Task<string> CreatePortalSessionAsync(int userId, bool fromPortal, CancellationToken ct = default);
}
