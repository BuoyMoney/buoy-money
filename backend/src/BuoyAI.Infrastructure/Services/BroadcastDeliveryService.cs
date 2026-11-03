using BuoyAI.Core.Utilities;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Drains queued admin broadcasts (#552). Sends one email per <see cref="BroadcastRecipient"/>
/// row in batches, updating per-recipient status as it goes — so a restart resumes exactly where
/// it left off and never double-sends (only <c>Pending</c> rows are picked up). Rate-limit (429)
/// responses trigger a backoff.
/// </summary>
public class BroadcastDeliveryService : BackgroundService
{
    private const string JobName = "broadcast_delivery";
    // Cap on consecutive rate-limit (429) retries within a single ProcessNextAsync run before we
    // yield the broadcast back to the outer loop (rows stay Pending, resumed later) — prevents a
    // provider outage from hot-looping this run forever.
    private const int MaxRateLimitRetries = 20;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BroadcastSettings _settings;
    private readonly ILogger<BroadcastDeliveryService> _logger;
    private readonly Telemetry.BuoyAiMetrics _metrics;

    public BroadcastDeliveryService(
        IServiceScopeFactory scopeFactory,
        IOptions<BroadcastSettings> settings,
        ILogger<BroadcastDeliveryService> logger,
        Telemetry.BuoyAiMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var didWork = await ProcessNextAsync(stoppingToken);
                // Only count a metered "run" when a broadcast was actually processed, so the
                // 10-second idle poll doesn't drown the success rate.
                if (didWork)
                {
                    _metrics.RecordBackgroundJobRun(JobName);
                    _metrics.RecordBackgroundJobSuccess(JobName);
                }
                // Back-to-back while there's work; otherwise idle-poll.
                if (!didWork)
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.RecordBackgroundJobRun(JobName);
                _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
                _logger.LogError(ex, "Broadcast delivery loop error");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailService>();

        // Oldest broadcast still carrying work. "Sending" is included so a restart resumes it.
        var broadcast = await db.Broadcasts
            .Where(b => b.Status == BroadcastStatus.Pending || b.Status == BroadcastStatus.Sending)
            .OrderBy(b => b.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (broadcast == null)
            return false;

        if (broadcast.Status == BroadcastStatus.Pending)
        {
            broadcast.Status = BroadcastStatus.Sending;
            broadcast.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var textBody = broadcast.BodyMarkdown
            + "\n\n---\nManage your email preferences: " + _settings.PreferencesUrl;

        var rateLimitRetries = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.BroadcastRecipients
                .Where(r => r.BroadcastId == broadcast.Id && r.Status == BroadcastRecipientStatus.Pending)
                .OrderBy(r => r.Id)
                .Take(_settings.BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            var rateLimited = false;
            foreach (var r in batch)
            {
                try
                {
                    await email.SendRawEmailAsync(r.Email, broadcast.Subject, broadcast.BodyHtml, textBody);
                    r.Status = BroadcastRecipientStatus.Sent;
                    r.SentAt = DateTime.UtcNow;
                    broadcast.SentCount++;
                }
                catch (Exception ex) when (IsRateLimited(ex))
                {
                    // Transient (429/rate limit): a rate limit is provider-wide and momentary, NOT a
                    // property of this recipient. Do NOT mark it Failed / increment FailedCount — the
                    // resume query only re-picks Pending rows, so a Failed row would drop the email
                    // permanently. Leave it (and the rest of the batch) Pending, back off, and retry.
                    rateLimitRetries++;
                    _logger.LogWarning(ex,
                        "Broadcast {Id} rate-limited for {Email}; leaving Pending to retry (attempt {Attempt}/{Max})",
                        broadcast.Id, r.Email, rateLimitRetries, MaxRateLimitRetries);
                    rateLimited = true;
                    break;
                }
                catch (Exception ex)
                {
                    r.Status = BroadcastRecipientStatus.Failed;
                    r.Error = Truncate(ex.Message, 500);
                    broadcast.FailedCount++;
                    _logger.LogWarning(ex, "Broadcast {Id} failed for {Email}", broadcast.Id, r.Email);
                }
            }

            await db.SaveChangesAsync(ct);

            if (rateLimited)
            {
                await Task.Delay(_settings.RateLimitBackoffMs, ct);
                if (rateLimitRetries >= MaxRateLimitRetries)
                {
                    // Provider has been rate-limiting for too long this run. Yield: the un-sent rows
                    // stay Pending and the outer loop resumes this broadcast (still Sending) later.
                    _logger.LogWarning(
                        "Broadcast {Id} still rate-limited after {Max} retries; yielding to resume later",
                        broadcast.Id, MaxRateLimitRetries);
                    return true;
                }
                continue; // re-query Pending (includes the un-sent recipient) and retry after backoff
            }

            await Task.Delay(_settings.DelayBetweenBatchesMs, ct);
        }

        var pendingLeft = await db.BroadcastRecipients
            .CountAsync(r => r.BroadcastId == broadcast.Id && r.Status == BroadcastRecipientStatus.Pending, ct);
        if (pendingLeft == 0)
        {
            broadcast.Status = (broadcast.SentCount == 0 && broadcast.FailedCount > 0)
                ? BroadcastStatus.Failed
                : BroadcastStatus.Completed;
            broadcast.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Broadcast {Id} {Status}: sent={Sent} failed={Failed} skipped={Skipped}",
                broadcast.Id, broadcast.Status, broadcast.SentCount, broadcast.FailedCount, broadcast.SkippedCount);
        }

        return true;
    }

    private static bool IsRateLimited(Exception ex)
    {
        var m = ex.Message?.ToLowerInvariant() ?? string.Empty;
        return m.Contains("429") || m.Contains("rate limit") || m.Contains("too many requests");
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : SafeText.Truncate(s, max);
}
