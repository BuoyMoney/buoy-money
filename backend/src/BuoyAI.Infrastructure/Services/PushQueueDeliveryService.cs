using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Sweeps the quiet-hours push queue every minute, delivering pushes whose DeliverAfter
/// has passed, and prunes stale device tokens (270+ days unseen) once a day. Idles cheaply
/// when neither Firebase nor the push relay is configured.
/// </summary>
public class PushQueueDeliveryService : BackgroundService
{
  private const string JobName = "PushQueueDelivery";
  private readonly TimeSpan _sweepInterval;

  private readonly IServiceScopeFactory _serviceScopeFactory;
  private readonly IFcmSender _fcmSender;
  private readonly ILogger<PushQueueDeliveryService> _logger;
  private readonly BuoyAiMetrics _metrics;
  private readonly IPushRelaySender? _relaySender;
  private DateTime _lastTokenPruneUtc = DateTime.MinValue;

  public PushQueueDeliveryService(
    IServiceScopeFactory serviceScopeFactory,
    IFcmSender fcmSender,
    ILogger<PushQueueDeliveryService> logger,
    BuoyAiMetrics metrics,
    IPushRelaySender? relaySender = null,
    TimeSpan? sweepInterval = null)
  {
    _sweepInterval = sweepInterval ?? TimeSpan.FromMinutes(1);
    _serviceScopeFactory = serviceScopeFactory;
    _fcmSender = fcmSender;
    _logger = logger;
    _metrics = metrics;
    _relaySender = relaySender;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_fcmSender.IsEnabled && _relaySender?.IsEnabled != true)
    {
      _logger.LogInformation("Push Queue Delivery Service idle: neither Firebase nor the push relay is configured");
      return;
    }

    _logger.LogInformation("Push Queue Delivery Service started");

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(_sweepInterval, stoppingToken);

        using var scope = _serviceScopeFactory.CreateScope();
        var pushService = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();

        var sent = await pushService.DeliverDueQueuedAsync(stoppingToken);
        if (sent > 0)
        {
          _metrics.RecordBackgroundJobRun(JobName);
          _metrics.RecordBackgroundJobSuccess(JobName);
          _logger.LogInformation("Delivered {Count} queued pushes", sent);
        }

        if (DateTime.UtcNow - _lastTokenPruneUtc > TimeSpan.FromHours(24))
        {
          _lastTokenPruneUtc = DateTime.UtcNow;
          await pushService.PruneStaleTokensAsync(stoppingToken);
        }
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
        _logger.LogError(ex, "Error in Push Queue Delivery Service sweep");
      }
    }

    _logger.LogInformation("Push Queue Delivery Service stopped");
  }
}
