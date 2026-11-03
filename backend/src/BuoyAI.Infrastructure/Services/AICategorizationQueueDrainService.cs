using System.Threading.Channels;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// In-process wake-up channel for <see cref="AICategorizationQueueDrainService"/>. Singleton;
/// ingest paths call <see cref="Notify"/> after queueing, the drainer owns <see cref="Reader"/>.
/// The durable queue is the AICategorizationQueue table, so a lost signal costs at most one
/// sweep interval, never a transaction.
/// </summary>
public class AICategorizationQueueSignal : IAICategorizationQueueSignal
{
  private readonly Channel<int> _channel =
    Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });

  public ChannelReader<int> Reader => _channel.Reader;

  public void Notify(int userId)
  {
    if (userId <= 0) return;
    _channel.Writer.TryWrite(userId);
  }
}

/// <summary>
/// The per-pass work of the drainer, split from the hosted loop so it can be unit tested with
/// an in-memory DbContext. Scoped: one instance per drain pass.
/// </summary>
public class AICategorizationQueueWorker
{
  public const int BatchSize = 20;
  public const int MaxBatchesPerUserPerPass = 25;
  public const int MaxRetries = 3;
  public const string NoConsentReason = "no_ai_consent";
  public static readonly TimeSpan RetryBackoff = TimeSpan.FromMinutes(5);

  private readonly BuoyAIDbContext _db;
  private readonly IAICategorizationService _categorization;
  private readonly ILogger<AICategorizationQueueWorker> _logger;

  public AICategorizationQueueWorker(
    BuoyAIDbContext db,
    IAICategorizationService categorization,
    ILogger<AICategorizationQueueWorker> logger)
  {
    _db = db;
    _categorization = categorization;
    _logger = logger;
  }

  public Task<List<int>> GetUsersWithPendingItemsAsync(CancellationToken cancellationToken) =>
    _db.AICategorizationQueue
      .Where(q => q.Status == AICategorizationQueueStatus.Pending)
      .Select(q => q.UserId)
      .Distinct()
      .ToListAsync(cancellationToken);

  /// <summary>
  /// Puts failed items back to Pending once their backoff has elapsed, up to
  /// <see cref="MaxRetries"/> attempts. Beyond that they stay Failed and the row is left for
  /// the manual Auto-Categorize path, which has its own attempt-once rule.
  /// </summary>
  public async Task<int> RequeueFailedAsync(CancellationToken cancellationToken)
  {
    var cutoff = DateTime.UtcNow - RetryBackoff;
    var retryable = await _db.AICategorizationQueue
      .Where(q => q.Status == AICategorizationQueueStatus.Failed
                  && q.RetryCount < MaxRetries
                  && (q.ProcessedAt == null || q.ProcessedAt < cutoff))
      .ToListAsync(cancellationToken);

    foreach (var item in retryable)
    {
      item.Status = AICategorizationQueueStatus.Pending;
      item.ErrorMessage = null;
    }

    if (retryable.Count > 0) await _db.SaveChangesAsync(cancellationToken);
    return retryable.Count;
  }

  /// <summary>
  /// Drains one user's pending items. Background AI work honours the same consent as the
  /// request path (5.1.2(i)): without consent the items are marked Skipped, not processed,
  /// and the manual button (which asks for consent first) remains available.
  /// Returns the number of transactions that received a suggestion.
  /// </summary>
  public async Task<int> DrainUserAsync(int userId, CancellationToken cancellationToken)
  {
    var consented = await _db.Users
      .AsNoTracking()
      .Where(u => u.Id == userId)
      .Where(AiConsent.IsCurrentExpr)
      .AnyAsync(cancellationToken);

    if (!consented)
    {
      var pending = await _db.AICategorizationQueue
        .Where(q => q.UserId == userId && q.Status == AICategorizationQueueStatus.Pending)
        .ToListAsync(cancellationToken);
      foreach (var item in pending)
      {
        item.Status = AICategorizationQueueStatus.Skipped;
        item.ErrorMessage = NoConsentReason;
        item.ProcessedAt = DateTime.UtcNow;
      }
      if (pending.Count > 0)
      {
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Skipped {Count} queued categorizations for user {UserId}: no AI consent", pending.Count, userId);
      }
      return 0;
    }

    var processed = 0;
    for (var batch = 0; batch < MaxBatchesPerUserPerPass; batch++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var remaining = await _categorization.GetQueuedTransactionCountAsync(userId);
      if (remaining == 0) break;

      // Every item taken by a batch leaves Pending (Completed, Failed or Skipped), so the
      // remaining count strictly decreases and this loop terminates without a guard on the
      // processed count.
      processed += await _categorization.ProcessCategorizationQueueAsync(userId, BatchSize);
    }

    if (processed > 0)
      _logger.LogInformation("Background AI categorization suggested {Count} categories for user {UserId}", processed, userId);

    return processed;
  }
}

/// <summary>
/// Drains the AI categorization queue off the request thread. Woken by
/// <see cref="AICategorizationQueueSignal"/> the moment an ingest path queues rows, and by a
/// periodic sweep for anything queued before a restart or whose signal was lost. Until this
/// existed the queue was drained only when a user opened the review sheet, so most synced
/// transactions sat on Plaid's guess indefinitely.
/// </summary>
public class AICategorizationQueueDrainService : BackgroundService
{
  private const string JobName = "ai_categorization_queue";
  private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
  // Syncs queue in bursts (one call per connection); wait a beat so one pass covers the burst.
  private static readonly TimeSpan Coalesce = TimeSpan.FromSeconds(3);

  private readonly AICategorizationQueueSignal _signal;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<AICategorizationQueueDrainService> _logger;
  private readonly BuoyAiMetrics _metrics;
  private readonly IAiProvider _ai;
  private bool _loggedAiOff;

  public AICategorizationQueueDrainService(
    AICategorizationQueueSignal signal,
    IServiceScopeFactory scopeFactory,
    ILogger<AICategorizationQueueDrainService> logger,
    BuoyAiMetrics metrics,
    IAiProvider ai)
  {
    _ai = ai;
    _signal = signal;
    _scopeFactory = scopeFactory;
    _logger = logger;
    _metrics = metrics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    try
    {
      await Task.Delay(StartupDelay, stoppingToken);
    }
    catch (OperationCanceledException)
    {
      return;
    }

    while (!stoppingToken.IsCancellationRequested)
    {
      var signaled = new HashSet<int>();
      try
      {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        wait.CancelAfter(SweepInterval);
        signaled.Add(await _signal.Reader.ReadAsync(wait.Token));
        await Task.Delay(Coalesce, stoppingToken);
        while (_signal.Reader.TryRead(out var more)) signaled.Add(more);
      }
      catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
      {
        // Sweep tick: no signal within the interval; still look for pending rows below.
      }
      catch (OperationCanceledException)
      {
        break;
      }

      await DrainAsync(signaled, stoppingToken);
    }
  }

  private async Task DrainAsync(HashSet<int> signaledUserIds, CancellationToken stoppingToken)
  {
    // AI off or not set up: leave the queue alone (no keyword guesses passed off as AI, no error
    // every sweep). Items stay Pending and drain once a provider is configured.
    if (!_ai.IsConfigured)
    {
      if (!_loggedAiOff)
      {
        _logger.LogInformation("AI categorization queue drain skipped: no AI provider is configured ({Provider})", _ai.Id);
        _loggedAiOff = true;
      }
      return;
    }


    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
      _metrics.RecordBackgroundJobRun(JobName);
      using var scope = _scopeFactory.CreateScope();
      var worker = scope.ServiceProvider.GetRequiredService<AICategorizationQueueWorker>();

      await worker.RequeueFailedAsync(stoppingToken);

      var users = new HashSet<int>(signaledUserIds);
      users.UnionWith(await worker.GetUsersWithPendingItemsAsync(stoppingToken));

      foreach (var userId in users)
      {
        if (stoppingToken.IsCancellationRequested) break;
        try
        {
          await worker.DrainUserAsync(userId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          break;
        }
        catch (Exception ex)
        {
          // One user's failure must not stop the pass for everyone else.
          _logger.LogError(ex, "Background AI categorization failed for user {UserId}", userId);
        }
      }

      stopwatch.Stop();
      _metrics.RecordBackgroundJobSuccess(JobName);
      _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // Shutting down.
    }
    catch (Exception ex)
    {
      stopwatch.Stop();
      _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
      _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
      _logger.LogError(ex, "AI categorization queue drain pass failed");
    }
  }
}
