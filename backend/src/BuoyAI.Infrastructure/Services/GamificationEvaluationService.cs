using System.Threading.Channels;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// In-process, unbounded queue of user ids awaiting gamification re-evaluation. Registered
/// as a singleton; write-path services depend on <see cref="IGamificationEvaluationQueue"/>
/// (Enqueue only) while <see cref="GamificationEvaluationService"/> reads the same instance.
/// </summary>
public class GamificationEvaluationQueue : IGamificationEvaluationQueue
{
  private readonly Channel<int> _channel =
    Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
  private readonly ILogger<GamificationEvaluationQueue> _logger;

  public GamificationEvaluationQueue(ILogger<GamificationEvaluationQueue> logger)
  {
    _logger = logger;
  }

  public ChannelReader<int> Reader => _channel.Reader;

  public void Enqueue(int userId)
  {
    if (userId <= 0) return;
    if (!_channel.Writer.TryWrite(userId))
    {
      _logger.LogWarning("Gamification evaluation queue rejected user {UserId}", userId);
    }
  }
}

/// <summary>
/// Drains <see cref="GamificationEvaluationQueue"/> and evaluates achievements + streaks for
/// each queued user off the request thread. This is what makes awards event-driven instead
/// of only firing when the Achievements page is opened. Evaluation is idempotent.
/// </summary>
public class GamificationEvaluationService : BackgroundService
{
  private const string JobName = "gamification_evaluation";
  private readonly GamificationEvaluationQueue _queue;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<GamificationEvaluationService> _logger;
  private readonly Telemetry.BuoyAiMetrics _metrics;

  public GamificationEvaluationService(
    GamificationEvaluationQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<GamificationEvaluationService> logger,
    Telemetry.BuoyAiMetrics metrics)
  {
    _queue = queue;
    _scopeFactory = scopeFactory;
    _logger = logger;
    _metrics = metrics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await foreach (var userId in _queue.Reader.ReadAllAsync(stoppingToken))
    {
      try
      {
        _metrics.RecordBackgroundJobRun(JobName);
        using var scope = _scopeFactory.CreateScope();
        var gamification = scope.ServiceProvider.GetRequiredService<IGamificationService>();
        await gamification.CheckAndAwardAchievementsAsync(userId);
        await gamification.UpdateStreaksAsync(userId);
        _metrics.RecordBackgroundJobSuccess(JobName);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        // A single user's evaluation failure must never stop the drain loop.
        _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
        _logger.LogError(ex, "Gamification evaluation failed for user {UserId}", userId);
      }
    }
  }
}
