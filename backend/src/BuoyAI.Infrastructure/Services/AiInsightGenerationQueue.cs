using BuoyAI.Core.Entities;
using System.Threading.Channels;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>A queued request to (re)generate AI insights for a user+context off the request thread.</summary>
public readonly record struct AiInsightJob(int UserId, string Context, int MaxInsights);

/// <summary>
/// In-process, unbounded queue of AI-insight generation jobs. Registered as a singleton;
/// read-path services depend on <see cref="IAiInsightQueue"/> (Enqueue only) while
/// <see cref="AiInsightGenerationBackgroundService"/> reads the same instance.
/// </summary>
public class AiInsightQueue : IAiInsightQueue
{
  private readonly Channel<AiInsightJob> _channel =
    Channel.CreateUnbounded<AiInsightJob>(new UnboundedChannelOptions { SingleReader = true });
  private readonly ILogger<AiInsightQueue> _logger;

  public AiInsightQueue(ILogger<AiInsightQueue> logger)
  {
    _logger = logger;
  }

  public ChannelReader<AiInsightJob> Reader => _channel.Reader;

  public void Enqueue(int userId, string context, int maxInsights)
  {
    if (userId <= 0 || string.IsNullOrWhiteSpace(context)) return;
    if (!_channel.Writer.TryWrite(new AiInsightJob(userId, context, maxInsights)))
    {
      _logger.LogWarning("AI insight queue rejected job for user {UserId}, context {Context}", userId, context);
    }
  }
}

/// <summary>
/// Drains <see cref="AiInsightQueue"/> and generates insights for each queued user+context
/// off the request thread — this is what keeps the slow Gemini call out of the HTTP path for
/// dashboard insights and savings overview. The reader is single-threaded and
/// <see cref="IAIInsightGenerationService.GenerateContextualInsightsAsync"/> is 24h-cache-guarded,
/// so if an earlier job already populated the cache a duplicate job returns without an LLM call.
/// </summary>
public class AiInsightGenerationBackgroundService : BackgroundService
{
  private const string JobName = "ai_insight_generation";
  private readonly AiInsightQueue _queue;
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<AiInsightGenerationBackgroundService> _logger;
  private readonly Telemetry.BuoyAiMetrics _metrics;

  public AiInsightGenerationBackgroundService(
    AiInsightQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<AiInsightGenerationBackgroundService> logger,
    Telemetry.BuoyAiMetrics metrics)
  {
    _queue = queue;
    _scopeFactory = scopeFactory;
    _logger = logger;
    _metrics = metrics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
    {
      try
      {
        _metrics.RecordBackgroundJobRun(JobName);
        using var scope = _scopeFactory.CreateScope();
        // 5.1.2(i): background generation must honour the same consent as the request path.
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await users.GetByIdAsync(job.UserId);
        if (!AiConsent.IsCurrent(user))
        {
          _logger.LogInformation("Skipping background AI insights for user {UserId}: no AI consent", job.UserId);
          continue;
        }
        var insights = scope.ServiceProvider.GetRequiredService<IAIInsightGenerationService>();
        await insights.GenerateContextualInsightsAsync(
          job.UserId,
          new AIInsightRequest { Context = job.Context, MaxInsights = job.MaxInsights });
        _metrics.RecordBackgroundJobSuccess(JobName);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        // A single job's failure must never stop the drain loop.
        _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
        _logger.LogError(ex, "Background AI insight generation failed for user {UserId}, context {Context}",
          job.UserId, job.Context);
      }
    }
  }
}
