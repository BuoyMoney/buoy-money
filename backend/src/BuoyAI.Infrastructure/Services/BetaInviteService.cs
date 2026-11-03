using System.Net;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Invites founding members to the self-host closed beta: every email with a paid, unrefunded reservation, once.
/// A send marks every reservation row of an email as invited before the email goes out, so a retry or a second
/// admin click never mails the same person twice. Emails are sent in reservation order (earliest first) and at most
/// <see cref="BetaOptions.MaxPerSend"/> per call.
/// </summary>
public class BetaInviteService : IBetaInviteService
{
    private readonly BuoyAIDbContext _db;
    private readonly IEmailService _email;
    private readonly BetaOptions _options;
    private readonly ILogger<BetaInviteService> _logger;

    public BetaInviteService(BuoyAIDbContext db, IEmailService email, IOptions<BetaOptions> options, ILogger<BetaInviteService> logger)
    {
        _db = db;
        _email = email;
        _options = options.Value;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public const string Subject = "Your founding spot: the Buoy self-host beta is open";

    public async Task<BetaInvitePreview> PreviewAsync(CancellationToken ct = default)
    {
        var (eligible, invited) = await CountsAsync(ct);
        return new BetaInvitePreview(eligible.Count, invited, Subject, TextBody());
    }

    // One send at a time per process; the claim below is the cross-process guard.
    private static readonly SemaphoreSlim SendGate = new(1, 1);

    public async Task<BetaInviteResult> SendAsync(int limit, bool dryRun, CancellationToken ct = default)
    {
        await SendGate.WaitAsync(ct);
        try
        {
            return await SendCoreAsync(limit, dryRun, ct);
        }
        finally
        {
            SendGate.Release();
        }
    }

    /// <summary>
    /// Marks every uninvited reservation row of an email as invited and returns how many rows this call won. On a
    /// relational database it is one conditional UPDATE (<c>BetaInvitedAt IS NULL</c>), so of two racing sends only one
    /// gets a non-zero count and only that one mails the person.
    /// </summary>
    private async Task<int> ClaimAsync(string email, DateTime now, CancellationToken ct)
    {
        var query = _db.Reservations.Where(r => r.Email != null && r.Email.ToLower() == email && r.BetaInvitedAt == null);
        if (_db.Database.IsRelational())
        {
            return await query.ExecuteUpdateAsync(s => s.SetProperty(r => r.BetaInvitedAt, now), ct);
        }
        var rows = await query.ToListAsync(ct);
        foreach (var row in rows)
        {
            row.BetaInvitedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task<BetaInviteResult> SendCoreAsync(int limit, bool dryRun, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 0, Math.Max(0, _options.MaxPerSend));
        var (eligible, _) = await CountsAsync(ct);
        var batch = eligible.Take(take).ToList();
        if (dryRun)
        {
            return new BetaInviteResult(true, 0, batch.Count, eligible.Count);
        }

        var now = DateTime.UtcNow;
        var sent = 0;
        foreach (var email in batch)
        {
            var claimed = await ClaimAsync(email, now, ct);
            if (claimed == 0)
            {
                continue; // someone else invited this email since the list was read
            }

            try
            {
                await _email.SendRawEmailAsync(email, Subject, HtmlBody(), TextBody());
                sent++;
            }
            catch (Exception ex)
            {
                // Marked first on purpose: a person who maybe got the email is not mailed again. Logged for a hand check.
                _logger.LogError(ex, "A beta invite email failed after the reservation was marked invited (email {Email})", email);
            }
        }

        _logger.LogInformation("Sent {Sent} beta invites", sent);
        return new BetaInviteResult(false, sent, batch.Count, eligible.Count - batch.Count);
    }

    /// <summary>Eligible emails (lower case, earliest reservation first) and the count already invited.</summary>
    private async Task<(List<string> Eligible, int Invited)> CountsAsync(CancellationToken ct)
    {
        var rows = await _db.Reservations.AsNoTracking()
            .Where(r => r.Email != null && r.Email != "")
            .Select(r => new { Email = r.Email!.ToLower(), r.Status, r.BetaInvitedAt, r.CreatedAt, r.Id })
            .ToListAsync(ct);

        var byEmail = rows.GroupBy(r => r.Email.Trim()).ToList();
        var invited = byEmail.Count(g => g.Any(r => r.BetaInvitedAt != null));
        var eligible = byEmail
            .Where(g => g.All(r => r.BetaInvitedAt == null) && g.Any(r => r.Status == ReservationStatus.Paid))
            .OrderBy(g => g.Min(r => r.CreatedAt)).ThenBy(g => g.Min(r => r.Id))
            .Select(g => g.Key)
            .ToList();
        return (eligible, invited);
    }

    private string TextBody()
    {
        var lines = new List<string>
        {
            "Hi,",
            "",
            "You reserved a founding spot for Buoy. The self-hosted edition is ready for a small group to try first, and you are in it.",
            "",
            "The self-hosted edition is free. It runs on your own computer or server with Docker, and your data stays there.",
            $"Install guide: {_options.InstallUrl}",
            "",
            $"If you want bank sync or AI from BuoyAI Cloud, sign up with this email address at {_options.SignUpUrl}. Your founding prices apply, and your $1 reservation comes off your first payment.",
            "",
            "This is a beta, so expect rough edges.",
        };
        if (!string.IsNullOrWhiteSpace(_options.FeedbackAddress))
        {
            lines.Add($"Tell us what breaks or confuses you at {_options.FeedbackAddress}. We read every message.");
            lines.Add("");
            lines.Add($"You are getting this because you reserved a founding spot. If you would rather have your $1 back, write to {_options.FeedbackAddress}.");
        }
        else
        {
            lines.Add("");
            lines.Add("You are getting this because you reserved a founding spot.");
        }
        lines.Add("");
        lines.Add("The Buoy Money team");
        return string.Join("\n", lines);
    }

    private string HtmlBody() =>
        string.Join("", TextBody().Split("\n\n").Select(p => $"<p>{WebUtility.HtmlEncode(p).Replace("\n", "<br>")}</p>"));
}
