using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services.Receipts;

/// <summary>
/// Every 15 minutes, matches receipts that were scanned before their bank charge synced, and
/// expires ones that waited too long. Registered with AddJobLockedHostedService so only one copy
/// runs during a blue-green deploy (it sends notifications).
/// </summary>
public class ReceiptRematchService : BackgroundService
{
  private const string JobName = "receipt_rematch";
  private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
  private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<ReceiptRematchService> _logger;
  private readonly BuoyAiMetrics _metrics;

  public ReceiptRematchService(IServiceScopeFactory scopeFactory, ILogger<ReceiptRematchService> logger, BuoyAiMetrics metrics)
  {
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

    using var timer = new PeriodicTimer(Interval);
    do
    {
      try
      {
        _metrics.RecordBackgroundJobRun(JobName);
        using var scope = _scopeFactory.CreateScope();
        var matched = await scope.ServiceProvider.GetRequiredService<ReceiptService>().RematchAsync(stoppingToken);
        if (matched > 0)
          _logger.LogInformation("Receipt rematch matched {Count} waiting receipts", matched);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Receipt rematch pass failed");
      }
    } while (await WaitAsync(timer, stoppingToken));
  }

  private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
  {
    try
    {
      return await timer.WaitForNextTickAsync(ct);
    }
    catch (OperationCanceledException)
    {
      return false;
    }
  }
}
