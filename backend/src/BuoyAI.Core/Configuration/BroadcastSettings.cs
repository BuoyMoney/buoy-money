namespace BuoyAI.Core.Configuration;

/// <summary>
/// Guardrails for the admin broadcast endpoint (#552). Bound from the "Broadcast" config
/// section; all values have safe defaults so a missing section is fine.
/// </summary>
public class BroadcastSettings
{
    /// <summary>
    /// Hard cap on recipients per broadcast. A send resolving to more than this is rejected
    /// (a deliberate blast-radius limit). Raise intentionally as the user base grows.
    /// </summary>
    public int MaxRecipients { get; set; } = 50_000;

    /// <summary>How many emails the delivery worker sends per batch before pausing.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Pause between batches (ms) to stay under Resend rate limits.</summary>
    public int DelayBetweenBatchesMs { get; set; } = 1_000;

    /// <summary>Backoff (ms) applied when Resend returns a 429 / rate-limit error.</summary>
    public int RateLimitBackoffMs { get; set; } = 5_000;

    /// <summary>
    /// Absolute URL of the in-app email-preferences page, linked in every broadcast footer
    /// so recipients can manage/opt out (compliance). Falls back to the app root if unset.
    /// </summary>
    public string PreferencesUrl { get; set; } = "https://buoy-ai.com/app/settings/email-preferences";
}
