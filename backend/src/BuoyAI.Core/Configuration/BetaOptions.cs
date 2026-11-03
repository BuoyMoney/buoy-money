namespace BuoyAI.Core.Configuration;

/// <summary>
/// The closed beta of the self-hosted edition (section <c>Beta</c>). Off by default: with it off the admin endpoints
/// answer 404 and no invite can be sent.
/// </summary>
public class BetaOptions
{
    public const string SectionName = "Beta";

    public bool Enabled { get; set; }

    /// <summary>The install guide the invite links to.</summary>
    public string InstallUrl { get; set; } = "https://buoy-ai.com/docs/selfhost/install";

    /// <summary>Where founding members sign up to buy Cloud at their founding prices.</summary>
    public string SignUpUrl { get; set; } = "https://buoy-ai.com/app/register";

    /// <summary>Where beta testers send problems and refund requests. Empty: the email leaves those lines out.</summary>
    public string FeedbackAddress { get; set; } = string.Empty;

    /// <summary>The most invites one send call may email, so a mistake cannot reach the whole list at once.</summary>
    public int MaxPerSend { get; set; } = 50;
}
