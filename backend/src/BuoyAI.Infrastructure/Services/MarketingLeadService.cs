using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <inheritdoc />
public class MarketingLeadService : IMarketingLeadService
{
    private readonly BuoyAIDbContext _context;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MarketingLeadService> _logger;

    public MarketingLeadService(
        BuoyAIDbContext context,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<MarketingLeadService> logger)
    {
        _context = context;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Only these sources are stored. An unrecognised value is dropped rather than
    /// persisted, so the column can't be used to smuggle arbitrary text in from a public
    /// endpoint and still means something when we compare trigger performance.
    /// </summary>
    private static readonly HashSet<string> AllowedSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "popup_timed",
        "popup_exit",
        "calculator_exit"
    };

    /// <summary>
    /// Leads captured before this instant were promised the old 60-day offer by email, and that
    /// promise is honoured (2026-10-14 owner decision). Newer leads get the current 30-day offer.
    /// </summary>
    public static readonly DateTime LegacyLeadCutoffUtc = new(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Longest trial a pre-cutoff lead promise can grant.</summary>
    public const int MaxLegacyLeadTrialDays = 60;

    public (int Standard, int Extended) GetOfferDays()
    {
        var standard = _configuration.GetValue<int>("Subscription:TrialPeriodDays", 14);
        var extended = _configuration.GetValue<int>("Subscription:ExtendedTrialPeriodDays", 30);
        // A misconfigured "extended" offer that is shorter than the standard one would make
        // the popup a downgrade. Never promise less than everyone already gets.
        return (standard, Math.Max(standard, extended));
    }

    public async Task<CaptureLeadResponse> CaptureAsync(CaptureLeadRequest request, CancellationToken ct = default)
    {
        var email = Normalize(request.Email);
        var (standard, extended) = GetOfferDays();

        var source = request.Source is not null && AllowedSources.Contains(request.Source)
            ? request.Source.ToLowerInvariant()
            : null;

        var snapshot = SerializeSnapshot(request.Calculator);
        var now = DateTime.UtcNow;

        // Can this address actually receive a trial? An address that already has an account,
        // or that has ever consumed its lifetime trial (TrialHistory survives account
        // deletion), never can. Storing a redeemable offer for one of those would promise
        // something the registration path will refuse, and the confirmation email would tell
        // an existing customer to go and sign up.
        //
        // The HTTP response is deliberately NOT branched on this — see CaptureLeadResponse.
        // Only the stored offer and the email body change, and the email is only ever seen by
        // whoever owns the address, so this leaks nothing to a prober.
        var isClaimable = await IsTrialClaimableAsync(email, ct);

        var existing = await _context.MarketingLeads
            .FirstOrDefaultAsync(l => l.Email == email, ct);

        if (existing != null)
        {
            // Repeat submit. Keep the ORIGINAL offer — resubmitting must not stack days —
            // but refresh the snapshot if this one carried calculator numbers and the
            // stored row had none, so the confirmation email can still be useful.
            existing.CaptureCount += 1;
            existing.UpdatedAt = now;
            if (snapshot != null && existing.CalculatorSnapshot == null)
            {
                existing.CalculatorSnapshot = snapshot;
            }
            if (source != null && existing.Source == null)
            {
                existing.Source = source;
            }
            // The address may have registered since the first capture. Retire the offer so a
            // stale row cannot outlive the eligibility that justified it.
            if (!isClaimable && existing.OfferedTrialDays > 0)
            {
                existing.OfferedTrialDays = 0;
            }
            await _context.SaveChangesAsync(ct);

            // Re-send only if the first attempt never landed; otherwise a visitor who
            // resubmits gets spammed with duplicates.
            // An unsubscribed address must never be mailed again, even if it re-submits the
            // form: honouring the opt-out is not conditional on them remembering they made it.
            if (existing.ConfirmationSentAt == null && existing.UnsubscribedAt == null)
            {
                await TrySendConfirmationAsync(existing, isClaimable, ct);
            }

            // Always report the advertised offer, never this address's stored state.
            //
            // Reflecting the row here leaked exactly what the identical-response rule exists
            // to hide: a redeemed or retired offer answered 14 where a new address answered
            // 30, so anyone could test whether an address had an account by watching the
            // number change. The popup only needs to know what to put on screen.
            return AdvertisedOffer(standard, extended);
        }

        var lead = new MarketingLead
        {
            Email = email,
            Source = source,
            // An unclaimable address is still recorded (it is a real marketing contact and a
            // real signal about who is reading the site) but carries a zero-day offer, so
            // GetUnredeemedOfferDaysAsync can never hand it to the registration path.
            OfferedTrialDays = isClaimable ? extended : 0,
            CalculatorSnapshot = snapshot,
            UnsubscribeToken = NewUnsubscribeToken(),
            CaptureCount = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.MarketingLeads.Add(lead);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two submits of the same address raced past the SELECT above. The unique index
            // did its job; treat it as the idempotent repeat it is.
            _context.Entry(lead).State = EntityState.Detached;
            _logger.LogInformation("Concurrent marketing-lead capture collapsed to one row");
            return AdvertisedOffer(standard, extended);
        }

        _logger.LogInformation(
            "Captured marketing lead from {Source} offering {Days} trial days", source ?? "unknown", extended);

        await TrySendConfirmationAsync(lead, isClaimable, ct);

        return AdvertisedOffer(standard, extended);
    }

    public async Task<int?> GetUnredeemedOfferDaysAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        if (normalized.Length == 0) return null;

        var lead = await _context.MarketingLeads
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Email == normalized
                                      && l.RedeemedAt == null
                                      // Zero-day rows are recorded contacts, not offers.
                                      && l.OfferedTrialDays > 0, ct);

        if (lead == null) return null;

        // Rows captured before the cutoff were promised up to 60 days by the old popup and keep
        // that promise. Anything newer is capped at the current extended offer, so a stale or
        // hand-edited row can never grant more than the popup advertises today.
        var cap = lead.CreatedAt < LegacyLeadCutoffUtc
            ? MaxLegacyLeadTrialDays
            : GetOfferDays().Extended;
        return Math.Min(lead.OfferedTrialDays, cap);
    }

    /// <summary>
    /// Whether this address could still claim a free trial.
    ///
    /// False when an account already exists for it, or when <c>TrialHistory</c> shows the
    /// lifetime trial was already consumed (that table outlives account deletion, which is
    /// what stops delete-and-re-register farming). Checking both is what keeps the popup from
    /// becoming a way around the one-trial-per-email rule: the offer only ever changes the
    /// REQUESTED length, and this makes sure we do not even record an offer we would refuse.
    /// </summary>
    private async Task<bool> IsTrialClaimableAsync(string normalizedEmail, CancellationToken ct)
    {
        var hasAccount = await _context.Users
            .AsNoTracking()
            .AnyAsync(u => u.Email == normalizedEmail, ct);
        if (hasAccount) return false;

        var hash = TrialEmailHasher.Compute(normalizedEmail);
        if (hash == null) return false;

        return !await _context.TrialHistories.AsNoTracking().AnyAsync(t => t.EmailHash == hash, ct);
    }

    public async Task MarkRedeemedAsync(string email, int userId, CancellationToken ct = default)
    {
        var normalized = Normalize(email);
        if (normalized.Length == 0) return;

        var lead = await _context.MarketingLeads
            .FirstOrDefaultAsync(l => l.Email == normalized && l.RedeemedAt == null, ct);
        if (lead == null) return;

        lead.RedeemedAt = DateTime.UtcNow;
        lead.RedeemedByUserId = userId;
        lead.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Redeemed extended trial offer of {Days} days for user {UserId}", lead.OfferedTrialDays, userId);
    }

    /// <summary>
    /// Sends the confirmation and records that it landed. A send failure must never fail the
    /// capture: we already hold the lead and the offer, and the partial index on
    /// confirmation_sent_at leaves the row retryable.
    /// </summary>
    private async Task TrySendConfirmationAsync(MarketingLead lead, bool isClaimable, CancellationToken ct)
    {
        try
        {
            var snapshot = DeserializeSnapshot(lead.CalculatorSnapshot);
            if (isClaimable)
            {
                await _emailService.SendExtendedTrialOfferAsync(
                    lead.Email, lead.OfferedTrialDays, GetOfferDays().Standard, snapshot,
                    lead.UnsubscribeToken);
            }
            else
            {
                // Already has an account, or already used the lifetime trial. Telling them
                // to "sign up and claim the extra trial" would be a promise we would then refuse at
                // registration, so send them somewhere useful instead.
                await _emailService.SendExistingAccountOfferAsync(
                    lead.Email, snapshot, lead.UnsubscribeToken);
            }

            lead.ConfirmationSentAt = DateTime.UtcNow;
            lead.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send extended-trial confirmation for lead {LeadId}", lead.Id);
        }
    }

    public async Task<bool> UnsubscribeAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var lead = await _context.MarketingLeads
            .FirstOrDefaultAsync(l => l.UnsubscribeToken == token, ct);
        if (lead == null) return false;

        // Idempotent: clicking the link twice is a success both times, not an error page.
        if (lead.UnsubscribedAt == null)
        {
            lead.UnsubscribedAt = DateTime.UtcNow;
            lead.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Marketing lead unsubscribed");
        }

        return true;
    }

    /// <summary>
    /// The public offer, identical for every caller.
    ///
    /// This is the whole anti-enumeration contract in one place: a brand-new address, a
    /// resubmit, an address that already has an account and one that burned its trial years
    /// ago all get the same bytes back. Whether the offer is actually claimable is decided
    /// server-side and communicated only by email, which only its owner can read.
    /// </summary>
    private static CaptureLeadResponse AdvertisedOffer(int standard, int extended) =>
        new() { TrialDays = extended, StandardTrialDays = standard };

    /// <summary>128 bits of randomness, hex, no dashes. Unguessable and URL-safe.</summary>
    private static string NewUnsubscribeToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..0];

    private static string Normalize(string? email) =>
        email?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? SerializeSnapshot(CalculatorSnapshotDto? snapshot) =>
        snapshot == null ? null : JsonSerializer.Serialize(snapshot);

    private static CalculatorSnapshotDto? DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CalculatorSnapshotDto>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
