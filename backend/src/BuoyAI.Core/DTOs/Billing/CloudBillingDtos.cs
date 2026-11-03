namespace BuoyAI.Core.DTOs.Billing;

// The wire contract with the BuoyAI Cloud billing service (JSON, camelCase). The service keeps a
// matching copy of these shapes; the two are kept in step by hand so that neither side references the
// other's code. The app only ever talks to Cloud over HTTP (see ICloudBillingClient).

public sealed record CloudProductSelection(string Product, int Quantity = 1);

public sealed record CloudCheckoutRequest(
    string CustomerId,
    int UserId,
    string Environment,
    string Interval,
    bool Founding,
    IReadOnlyList<CloudProductSelection> Products,
    string SuccessUrl,
    string CancelUrl,
    string? IdempotencyKey = null,
    long? FoundingCreditCents = null);

public sealed record CloudCheckoutResponse(string Url, string SessionId);

public sealed record CloudChangeRequest(
    string CustomerId,
    string Environment,
    string Type,
    string? Product = null,
    int? Quantity = null,
    DateTime? ProrationDate = null,
    string? IdempotencyKey = null);

public sealed record CloudProductState(
    string Product,
    int Quantity,
    IReadOnlyList<string> Entitlements,
    DateTime? EndsAt,
    int? QuantityAfterPeriodEnd);

public sealed record CloudItemState(string Product, string LookupKey, int Quantity, long UnitAmountCents, string Currency);

public sealed record CloudPendingChange(string Product, string Kind, int? QuantityAfter, DateTime? At);

public sealed record CloudSubscriptionState(
    string SubscriptionId,
    string Status,
    string Interval,
    bool Founding,
    DateTime? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    IReadOnlyList<CloudProductState> Products,
    IReadOnlyList<CloudItemState> Items,
    IReadOnlyList<CloudPendingChange> Pending);

public sealed record CloudPreviewLine(string Description, long AmountCents, bool Proration);

public sealed record CloudPreview(
    string Type,
    string Effective,
    DateTime? EffectiveAt,
    long ProrationTotalCents,
    long NextInvoiceTotalCents,
    string Currency,
    DateTime ProrationDate,
    IReadOnlyList<CloudPreviewLine> Lines,
    IReadOnlyList<CloudProductState> ProductsAfter);

public sealed record CloudPriceView(string LookupKey, long AmountCents, string Currency, int Version);

public sealed record CloudCatalogProduct(
    string Name,
    string DisplayName,
    IReadOnlyList<string> Entitlements,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> BundleOf,
    int? MinQuantity,
    int? MaxQuantity,
    int? IncludedLogins,
    int? IncludedRefreshesPerMonth,
    CloudPriceView? Monthly,
    CloudPriceView? Yearly);

public sealed record CloudCatalog(string Version, bool Founding, string Currency, IReadOnlyList<CloudCatalogProduct> Products);

/// <summary>What the app tells its own clients about a user's Cloud plan.</summary>
/// <param name="SubscriptionLookupFailed">
/// The billing service could not be asked, so <c>Subscription</c> is unknown rather than absent.
/// <c>Access</c> comes from the app's own database and is correct either way.
/// </param>
/// <param name="HasLivePlan">
/// The user holds an Active or PastDue plan from any provider, so a first-purchase checkout would be refused (409).
/// </param>
public sealed record CloudBillingStatus(
    bool Enabled,
    bool FoundingEligible,
    CloudSubscriptionState? Subscription,
    CloudProductAccess Access,
    bool SubscriptionLookupFailed = false,
    bool HasLivePlan = false);

/// <summary>The products a user may use right now (see <c>ProductAccess</c>).</summary>
public sealed record CloudProductAccess(
    bool BankSync,
    bool Ai,
    bool InstantRefresh,
    int ExtraLogins,
    IReadOnlyDictionary<string, DateTime> EndsAt,
    bool Founding);

/// <summary>
/// One line of the next invoice. <c>Kind</c> is "product" (<c>Product</c> names the catalog item), "ai_overage",
/// "refresh_overage" or "other". <c>UnitAmountCents</c> is 0 where Stripe has no per-unit price (usage, one-off items).
/// </summary>
public sealed record CloudUpcomingLine(
    string Kind, string? Product, string Description, long Quantity, long UnitAmountCents, long AmountCents, bool Proration);

public sealed record CloudUpcomingInvoice(
    string SubscriptionId,
    string Status,
    string Interval,
    bool Founding,
    bool CancelAtPeriodEnd,
    DateTime? NextInvoiceAt,
    string Currency,
    long TotalCents,
    IReadOnlyList<CloudUpcomingLine> Lines);

public sealed record CloudPortalSessionRequest(string CustomerId, string Environment, string ReturnUrl);

public sealed record CloudPortalSession(string Url);

/// <summary>
/// The billing screen's data, the same shape on the hosted service and on a self-hosted server.
/// <c>Subscription</c> is the plan (null: no Cloud plan), <c>Upcoming</c> the next invoice line by line.
/// <c>LookupFailed</c> means the billing service could not be asked, so both are unknown rather than absent.
/// </summary>
public sealed record CloudBillingSummary(
    CloudSubscriptionState? Subscription,
    CloudUpcomingInvoice? Upcoming,
    bool LookupFailed = false);
