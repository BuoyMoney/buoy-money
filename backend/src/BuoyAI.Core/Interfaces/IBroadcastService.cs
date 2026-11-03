using BuoyAI.Core.DTOs.Admin;

namespace BuoyAI.Core.Interfaces;

/// <summary>Outcome of a broadcast create/preview. Success=false carries a user-facing Error.</summary>
public record BroadcastResult(bool Success, string? Error, BroadcastResponse? Response);

/// <summary>Admin broadcast / incident email (#552).</summary>
public interface IBroadcastService
{
    /// <summary>
    /// Dry run (count + preview, no send) or a real send (persists a durable broadcast +
    /// per-recipient rows for the background worker to deliver). Enforces the recipient cap,
    /// the confirm-count guardrail, audience/preference filtering, and idempotency.
    /// </summary>
    Task<BroadcastResult> CreateAsync(BroadcastRequest request, int adminUserId, string adminUsername, CancellationToken ct = default);

    /// <summary>Draft a subject + body from a one-line prompt (AI; returned for review, never sent).</summary>
    Task<BroadcastDraftResponse> DraftAsync(BroadcastDraftRequest request, CancellationToken ct = default);
}
