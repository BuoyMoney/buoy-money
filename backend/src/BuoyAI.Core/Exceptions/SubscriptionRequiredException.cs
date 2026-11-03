namespace BuoyAI.Core.Exceptions;

/// <summary>
/// Exception thrown when a user attempts to access a feature that requires a premium subscription.
/// This allows controllers to identify subscription-related authorization failures without
/// relying on fragile string matching on exception messages.
/// </summary>
public class SubscriptionRequiredException : Exception
{
    /// <summary>
    /// The subscription tier required to access the feature
    /// </summary>
    public string RequiredTier { get; }

    /// <summary>
    /// The feature that was attempted to be accessed
    /// </summary>
    public string Feature { get; }

    public SubscriptionRequiredException(string feature, string requiredTier = "Premium")
        : base($"{feature} require a {requiredTier} subscription. Please upgrade your plan to access this feature.")
    {
        Feature = feature;
        RequiredTier = requiredTier;
    }

    public SubscriptionRequiredException(string message, string feature, string requiredTier)
        : base(message)
    {
        Feature = feature;
        RequiredTier = requiredTier;
    }

    public SubscriptionRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
        Feature = "Unknown";
        RequiredTier = "Premium";
    }
}
