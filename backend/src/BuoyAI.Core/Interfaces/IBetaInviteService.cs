namespace BuoyAI.Core.Interfaces;

/// <param name="Eligible">Distinct emails with a paid, unrefunded reservation that have not been invited.</param>
/// <param name="AlreadyInvited">Distinct emails already invited.</param>
public sealed record BetaInvitePreview(int Eligible, int AlreadyInvited, string Subject, string TextBody);

/// <param name="Sent">Emails sent by this call (0 on a dry run).</param>
/// <param name="WouldSend">How many this call would send, or did.</param>
/// <param name="Remaining">Eligible emails left after this call.</param>
public sealed record BetaInviteResult(bool DryRun, int Sent, int WouldSend, int Remaining);

/// <summary>Invites founding members (from Reservations) to the self-host closed beta. Off unless <c>Beta:Enabled</c>.</summary>
public interface IBetaInviteService
{
    bool Enabled { get; }
    Task<BetaInvitePreview> PreviewAsync(CancellationToken ct = default);
    Task<BetaInviteResult> SendAsync(int limit, bool dryRun, CancellationToken ct = default);
}
