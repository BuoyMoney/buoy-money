using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Admin;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Email;
using Markdig;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Builds and persists admin broadcasts (#552). Delivery itself is handled asynchronously by
/// <see cref="BroadcastDeliveryService"/>; this service only resolves recipients, renders the
/// email, enforces guardrails, and queues durable work.
/// </summary>
public class BroadcastService : IBroadcastService
{
    // Pipe tables on, so a "| a | b |" table in the body becomes a real table in the email.
    // Raw HTML is escaped: the admin's Flutter preview never renders HTML, so the sent email must not either.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();

    private readonly BuoyAIDbContext _db;
    private readonly BroadcastSettings _settings;
    private readonly IAIService _ai;
    private readonly ILogger<BroadcastService> _logger;

    public BroadcastService(
        BuoyAIDbContext db,
        IOptions<BroadcastSettings> settings,
        IAIService ai,
        ILogger<BroadcastService> logger)
    {
        _db = db;
        _settings = settings.Value;
        _ai = ai;
        _logger = logger;
    }

    public async Task<BroadcastResult> CreateAsync(
        BroadcastRequest request, int adminUserId, string adminUsername, CancellationToken ct = default)
    {
        if (!TryParseAudience(request.Audience, out var audience))
            return Fail($"Invalid audience '{request.Audience}'. Use 'all' or 'active'.");
        if (!TryParseCategory(request.Category, out var category))
            return Fail($"Invalid category '{request.Category}'. Use 'incident' or 'announcement'.");
        if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.BodyMarkdown))
            return Fail("Subject and bodyMarkdown are required.");

        var (bodyHtml, _) = Render(request.Subject, request.BodyMarkdown, category);

        // Resolve recipients: audience filter + announcement opt-out (incidents override opt-outs).
        var (willReceive, skipped) = await ResolveRecipientsAsync(audience, category, ct);

        if (request.DryRun)
        {
            return Ok(new BroadcastResponse
            {
                DryRun = true,
                Status = "preview",
                RecipientCount = willReceive.Count,
                SkippedCount = skipped,
                PreviewHtml = bodyHtml,
                Message = $"Would send to {willReceive.Count} recipient(s); {skipped} opted out. "
                        + "Re-submit with dryRun=false and confirmRecipientCount set to this count to send.",
            });
        }

        // ---- real send: guardrails ----
        if (willReceive.Count == 0)
            return Fail("No recipients match this audience/category.");
        if (willReceive.Count > _settings.MaxRecipients)
            return Fail($"Recipient count {willReceive.Count} exceeds the configured MaxRecipients cap ({_settings.MaxRecipients}).");
        if (request.ConfirmRecipientCount != willReceive.Count)
        {
            return Fail($"confirmRecipientCount ({request.ConfirmRecipientCount?.ToString() ?? "null"}) must equal the "
                      + $"current recipient count ({willReceive.Count}). Re-run a dry run and confirm.");
        }

        // Idempotency: a real send with a previously-used key returns the existing broadcast.
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? Guid.NewGuid().ToString("N")
            : request.IdempotencyKey.Trim();
        var existing = await _db.Broadcasts.AsNoTracking().FirstOrDefaultAsync(b => b.IdempotencyKey == key, ct);
        if (existing != null)
        {
            _logger.LogInformation("Broadcast idempotency hit for key {Key}: returning {Id}", key, existing.Id);
            return Ok(ToResponse(existing));
        }

        var broadcast = new Broadcast
        {
            Id = Guid.NewGuid(),
            Subject = request.Subject.Trim(),
            BodyMarkdown = request.BodyMarkdown,
            BodyHtml = bodyHtml,
            Category = category,
            Audience = audience,
            Status = BroadcastStatus.Pending,
            TotalRecipients = willReceive.Count,
            SkippedCount = skipped,
            CreatedByUserId = adminUserId,
            CreatedByUsername = adminUsername,
            IdempotencyKey = key,
            Recipients = willReceive
                .Select(r => new BroadcastRecipient { UserId = r.UserId, Email = r.Email, Status = BroadcastRecipientStatus.Pending })
                .ToList(),
        };

        _db.Broadcasts.Add(broadcast);
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "ADMIN BROADCAST queued: id={Id} category={Category} audience={Audience} recipients={Count} skipped={Skipped} by={User}({UserId}) subject={Subject}",
            broadcast.Id, category, audience, willReceive.Count, skipped, adminUsername, adminUserId, broadcast.Subject);

        return Ok(ToResponse(broadcast));
    }

    public async Task<BroadcastDraftResponse> DraftAsync(BroadcastDraftRequest request, CancellationToken ct = default)
    {
        var tone = string.Equals(request.Category, "incident", StringComparison.OrdinalIgnoreCase)
            ? "a calm, factual incident/status update (no marketing language)"
            : "a friendly, concise product announcement";

        var prompt =
            $"You are drafting a transactional email for Buoy Money, a personal-finance app, to send to users. "
            + $"Write {tone} based on this request: \"{request.Prompt}\".\n"
            + "Respond in EXACTLY this format and nothing else:\n"
            + "Subject: <one concise subject line>\n\n"
            + "<the email body in Markdown>";

        var text = (await _ai.GenerateTextAsync(prompt) ?? string.Empty).Trim();
        var (subject, body) = ParseDraft(text, request.Prompt);
        return new BroadcastDraftResponse { Subject = subject, BodyMarkdown = body };
    }

    // ---- helpers ----

    private async Task<(List<(int UserId, string Email)> WillReceive, int Skipped)> ResolveRecipientsAsync(
        BroadcastAudience audience, BroadcastCategory category, CancellationToken ct)
    {
        // Never email unverified addresses (bounce/reputation risk).
        var q = _db.Users.Where(u => !u.IsDeleted && u.IsEmailVerified && u.Email != "");
        if (audience == BroadcastAudience.Active)
            q = q.Where(u => u.SubscriptionTier != null || u.IsTrialActive);

        // Left join preferences; users with no row default to ProductAnnouncements = true (opt-out model).
        var rows = await (
            from u in q
            join p in _db.UserEmailPreferences on u.Id equals p.UserId into pg
            from p in pg.DefaultIfEmpty()
            select new { u.Id, u.Email, Announce = p == null || p.ProductAnnouncements })
            .ToListAsync(ct);

        if (category == BroadcastCategory.Incident)
        {
            // Incidents are legitimate service comms — override marketing opt-outs.
            return (rows.Select(r => (r.Id, r.Email)).ToList(), 0);
        }

        var willReceive = rows.Where(r => r.Announce).Select(r => (r.Id, r.Email)).ToList();
        var skipped = rows.Count - willReceive.Count;
        return (willReceive, skipped);
    }

    private (string Html, string Text) Render(string subject, string bodyMarkdown, BroadcastCategory category)
    {
        var bodyHtml = Markdown.ToHtml(bodyMarkdown ?? string.Empty, Pipeline);
        var footer = category == BroadcastCategory.Incident
            ? "You're receiving this service update because you have a Buoy Money account."
            : $"You're receiving Buoy Money product announcements. Manage your email preferences at {_settings.PreferencesUrl}.";
        var emoji = category == BroadcastCategory.Incident ? "🛠️" : "📣";
        var html = EmailTemplateBase.WrapInTemplate(subject, emoji, bodyHtml, footer);
        var text = (bodyMarkdown ?? string.Empty) + "\n\n---\n" + footer;
        return (html, text);
    }

    private static (string Subject, string Body) ParseDraft(string text, string fallbackPrompt)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ($"Buoy Money: {fallbackPrompt}".Trim(), fallbackPrompt);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var subjectIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("Subject:", StringComparison.OrdinalIgnoreCase));
        if (subjectIdx >= 0)
        {
            var subject = lines[subjectIdx].TrimStart()["Subject:".Length..].Trim();
            var body = string.Join("\n", lines.Skip(subjectIdx + 1)).Trim();
            if (string.IsNullOrWhiteSpace(body)) body = text;
            return (string.IsNullOrWhiteSpace(subject) ? $"Buoy Money: {fallbackPrompt}".Trim() : subject, body);
        }
        return ($"Buoy Money: {fallbackPrompt}".Trim(), text);
    }

    private static bool TryParseAudience(string? s, out BroadcastAudience a)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "all": a = BroadcastAudience.All; return true;
            case "active": a = BroadcastAudience.Active; return true;
            default: a = BroadcastAudience.All; return false;
        }
    }

    private static bool TryParseCategory(string? s, out BroadcastCategory c)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "incident": c = BroadcastCategory.Incident; return true;
            case "announcement": c = BroadcastCategory.Announcement; return true;
            default: c = BroadcastCategory.Announcement; return false;
        }
    }

    private static BroadcastResponse ToResponse(Broadcast b) => new()
    {
        BroadcastId = b.Id,
        DryRun = false,
        Status = b.Status.ToString().ToLowerInvariant(),
        RecipientCount = b.TotalRecipients,
        SkippedCount = b.SkippedCount,
        Message = $"Broadcast {b.Id} queued for delivery.",
    };

    private static BroadcastResult Ok(BroadcastResponse r) => new(true, null, r);
    private static BroadcastResult Fail(string error) => new(false, error, null);
}
