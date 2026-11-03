namespace BuoyAI.Core.Enums;

/// <summary>
/// AI interaction mode for chat responses
/// </summary>
public enum AIInteractionMode
{
    /// <summary>
    /// AI uses sensible defaults but still confirms before write operations.
    /// Faster interaction with less back-and-forth.
    /// </summary>
    Quick = 0,

    /// <summary>
    /// AI asks clarifying questions for ambiguous inputs AND confirms before write operations.
    /// More thorough but requires more user input.
    /// </summary>
    Clarifying = 1
}

/// <summary>
/// How much the AI does autonomously — the "AI Control Spectrum" (issue #412).
/// Orthogonal to <see cref="AIInteractionMode"/> (which controls chat chattiness):
/// this controls how proactive/automatic the AI is across the app.
///
/// Each level gates concrete, wired behavior — see AIService/AICategorizationService/
/// AIInsightGenerationService where this is read. Higher levels are supersets of lower.
/// </summary>
public enum AIAutonomyLevel
{
    /// <summary>
    /// AI is silent outside of explicit chat. No insight generation, no auto-categorization,
    /// no suggestions. Writes still require confirmation. For self-managers / AI-skeptics.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// AI observes and surfaces optional insights (the existing AI Insights cards), but takes
    /// no automatic actions. Categorization stays manual/review. Default for existing users
    /// (matches prior behavior: insights on, no auto-actions).
    /// </summary>
    Coach = 1,

    /// <summary>
    /// Coach + proactively suggests concrete next steps in chat for the user to approve.
    /// (Auto-categorization-on-sync, #412 Goal 1B, was deferred — Assistant is suggestion-based,
    /// not auto-acting, for now.)
    /// </summary>
    Assistant = 2,

    /// <summary>
    /// RESERVED — not yet exposed in the client. Intended top of the spectrum: fully autonomous
    /// (auto-categorize on sync, weekly brief, acts within guardrails). Activates once that
    /// proactive layer (ProactiveInsightService #412 Goal 1A + auto-categorization #412 Goal 1B)
    /// is built. Kept in the enum for forward-compatibility.
    /// </summary>
    Manager = 3
}
