namespace BuoyAI.Core.Entities;

/// <summary>How a broadcast is treated w.r.t. email preferences.</summary>
public enum BroadcastCategory
{
    /// <summary>Incident/status comms — legitimate service email; overrides marketing opt-outs.</summary>
    Incident = 0,

    /// <summary>Product announcement — respects the (default-on) ProductAnnouncements opt-out.</summary>
    Announcement = 1,
}

/// <summary>Who a broadcast targets.</summary>
public enum BroadcastAudience
{
    /// <summary>Every non-deleted, email-verified user.</summary>
    All = 0,

    /// <summary>Verified users with a subscription or a live trial.</summary>
    Active = 1,
}

public enum BroadcastStatus
{
    Pending = 0,
    Sending = 1,
    Completed = 2,
    Failed = 3,
}

public enum BroadcastRecipientStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Skipped = 3, // opted out (announcement) — recorded for audit, never sent
}

/// <summary>
/// A durable admin broadcast job (#552). Persisted so delivery survives restarts, is resumable,
/// idempotent (unique IdempotencyKey), and fully audited (who/when/subject/counts). A background
/// worker drains <see cref="BroadcastRecipient"/> rows in batches.
/// </summary>
public class Broadcast
{
    public Guid Id { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;

    public BroadcastCategory Category { get; set; }
    public BroadcastAudience Audience { get; set; }
    public BroadcastStatus Status { get; set; } = BroadcastStatus.Pending;

    public int TotalRecipients { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }

    // Audit: who triggered it.
    public int CreatedByUserId { get; set; }
    public string CreatedByUsername { get; set; } = string.Empty;

    /// <summary>Idempotency guard — a retried request with the same key returns the existing broadcast.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }

    public ICollection<BroadcastRecipient> Recipients { get; set; } = new List<BroadcastRecipient>();
}

/// <summary>
/// Per-recipient delivery row for a <see cref="Broadcast"/>. Enables exact resume after a restart
/// and prevents double-sends (a row is only sent once; status transitions Pending → Sent/Failed/Skipped).
/// </summary>
public class BroadcastRecipient
{
    public long Id { get; set; }

    public Guid BroadcastId { get; set; }
    public Broadcast Broadcast { get; set; } = null!;

    public int UserId { get; set; }
    public string Email { get; set; } = string.Empty;

    public BroadcastRecipientStatus Status { get; set; } = BroadcastRecipientStatus.Pending;
    public string? Error { get; set; }
    public DateTime? SentAt { get; set; }
}
