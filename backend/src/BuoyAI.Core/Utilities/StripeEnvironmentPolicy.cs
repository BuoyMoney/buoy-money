namespace BuoyAI.Core.Utilities;

/// <summary>
/// Decides which Stripe objects a given environment is allowed to touch.
///
/// Staging and production point at the SAME live Stripe account, so a Stripe customer is
/// a shared resource rather than a per-environment one. Two paths made that dangerous:
/// customer lookup by email would let staging adopt production's customer for the same
/// person, and account deletion removes whatever customer id the local row happens to
/// hold (which cancels its subscriptions and detaches its payment methods). Together,
/// deleting a staging account could delete the production customer behind the same email.
///
/// Every customer and subscription we create is now stamped with <see cref="MetadataKey"/>,
/// and both paths refuse to act on an object belonging to a different environment. This is
/// the same collision problem <c>ChatEmbeddingService</c> solves by namespacing its Qdrant
/// collection per environment; Stripe has no namespace, so the tag lives in metadata.
/// </summary>
public static class StripeEnvironmentPolicy
{
    /// <summary>Metadata key stamped on every customer and subscription we create.</summary>
    public const string MetadataKey = "environment";

    /// <summary>The tag production runs under. Legacy untagged objects are treated as its own.</summary>
    public const string ProductionTag = "production";

    /// <summary>
    /// Normalizes an ASP.NET environment name into the tag we write to Stripe metadata.
    /// Blank input resolves to something that is deliberately NOT production, so a
    /// misconfigured host fails closed and declines to touch shared objects.
    /// </summary>
    public static string Tag(string? environmentName) =>
        string.IsNullOrWhiteSpace(environmentName)
            ? "unknown"
            : environmentName.Trim().ToLowerInvariant();

    /// <summary>
    /// True when the current environment may adopt or delete this Stripe object.
    ///
    /// An object tagged for this environment is ours. An object tagged for a different one
    /// never is. An UNTAGGED object predates this policy, and everything predating it was
    /// created by production or by hand in the dashboard, so only production may claim it.
    /// Staging therefore leaves untagged customers alone: orphaning a staging customer is
    /// recoverable, deleting a live production customer is not.
    /// </summary>
    public static bool IsOwnedBy(IDictionary<string, string>? metadata, string currentTag)
    {
        var owner = Owner(metadata);
        return owner == null
            ? string.Equals(currentTag, ProductionTag, StringComparison.OrdinalIgnoreCase)
            : string.Equals(owner, currentTag, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The environment that created this object, or null when it is untagged. Exposed so
    /// callers can name the real owner in a log line instead of just "not ours".
    /// </summary>
    public static string? Owner(IDictionary<string, string>? metadata)
    {
        if (metadata == null) return null;
        if (!metadata.TryGetValue(MetadataKey, out var owner)) return null;
        return string.IsNullOrWhiteSpace(owner) ? null : owner.Trim().ToLowerInvariant();
    }
}
