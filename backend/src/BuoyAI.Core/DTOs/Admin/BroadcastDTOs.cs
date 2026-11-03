using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.DTOs.Admin;

/// <summary>Request to send (or preview) an admin broadcast email (#552).</summary>
public class BroadcastRequest
{
    [Required, MaxLength(300)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string BodyMarkdown { get; set; } = string.Empty;

    /// <summary>"all" or "active".</summary>
    [Required]
    public string Audience { get; set; } = "all";

    /// <summary>"incident" or "announcement".</summary>
    [Required]
    public string Category { get; set; } = "announcement";

    /// <summary>When true, returns recipient count + rendered preview and sends nothing.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// Safety confirm: on a real send this must equal the recipient count returned by the
    /// matching dry run. Mismatch (or null) is rejected. Ignored when DryRun is true.
    /// </summary>
    public int? ConfirmRecipientCount { get; set; }

    /// <summary>
    /// Optional idempotency key. A real send with a key already used returns the existing
    /// broadcast instead of sending again. Auto-derived if omitted.
    /// </summary>
    [MaxLength(128)]
    public string? IdempotencyKey { get; set; }
}

/// <summary>Result of a broadcast request (dry run or real send).</summary>
public class BroadcastResponse
{
    public Guid? BroadcastId { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool DryRun { get; set; }

    /// <summary>Users who will receive (or were queued for) the email.</summary>
    public int RecipientCount { get; set; }

    /// <summary>Users in the audience who were skipped (e.g. opted out of announcements).</summary>
    public int SkippedCount { get; set; }

    /// <summary>Rendered HTML preview (dry run only).</summary>
    public string? PreviewHtml { get; set; }

    public string? Message { get; set; }
}

/// <summary>Ask the AI to draft a subject + body from a one-line prompt (never auto-sent).</summary>
public class BroadcastDraftRequest
{
    [Required]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Optional tone hint: "incident" or "announcement".</summary>
    public string? Category { get; set; }
}

public class BroadcastDraftResponse
{
    public string Subject { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
}
