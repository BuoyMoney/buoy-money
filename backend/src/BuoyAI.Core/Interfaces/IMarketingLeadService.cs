using BuoyAI.Core.DTOs;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Capture and redemption of marketing-site email leads that carry an extended free-trial
/// offer.
/// </summary>
public interface IMarketingLeadService
{
    /// <summary>
    /// Records an email captured on the marketing site and returns the trial length that
    /// address will now receive. Idempotent: re-submitting the same address bumps a counter
    /// and returns the offer it already holds rather than stacking a new one.
    /// </summary>
    Task<CaptureLeadResponse> CaptureAsync(CaptureLeadRequest request, CancellationToken ct = default);

    /// <summary>
    /// The unredeemed offer for this address in days, or null when there is none. Called on
    /// the registration path to decide the trial length.
    /// </summary>
    Task<int?> GetUnredeemedOfferDaysAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Marks the offer for this address consumed by <paramref name="userId"/>. No-op when
    /// there is no unredeemed offer, so callers need not check first.
    /// </summary>
    Task MarkRedeemedAsync(string email, int userId, CancellationToken ct = default);

    /// <summary>
    /// Opts a lead out of all further marketing email. Returns false only when the token is
    /// unknown. Idempotent, so a second click on the same link still reports success.
    /// </summary>
    Task<bool> UnsubscribeAsync(string token, CancellationToken ct = default);

    /// <summary>The configured standard and extended trial lengths, in days.</summary>
    (int Standard, int Extended) GetOfferDays();
}
