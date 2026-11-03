namespace BuoyAI.Core.Enums;

/// <summary>
/// Which slice of the review backlog to surface (#716-followup / review-modal clarity).
/// The review flow defaults to <see cref="ThisMonth"/> so users aren't handed their whole
/// history at once, then lets them explicitly page into <see cref="Older"/>.
/// </summary>
public enum ReviewScope
{
    /// <summary>Everything needing review, regardless of date (the full backlog).</summary>
    All = 0,

    /// <summary>Only transactions dated in the current calendar month.</summary>
    ThisMonth = 1,

    /// <summary>Only transactions dated before the current calendar month (the historical backlog).</summary>
    Older = 2,
}
