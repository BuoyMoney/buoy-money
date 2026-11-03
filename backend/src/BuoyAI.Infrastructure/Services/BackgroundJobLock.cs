using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Decides which running copy of the API runs the scheduled background jobs.
///
/// Prod deploys are blue-green: for about a minute the new copy runs next to the old one,
/// against the same database. Scheduled jobs (push delivery, reminder emails, referral
/// coupons, Plaid syncs, ...) don't coordinate with each other, so two copies would each
/// send their own. Only the copy holding the job lock runs them; the other waits and takes
/// over when the holder stops.
/// </summary>
public interface IBackgroundJobLock
{
  /// <summary>True while this copy holds the lock.</summary>
  bool IsHeld { get; }

  /// <summary>Completes once this copy holds the lock.</summary>
  Task WaitUntilHeldAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Single-copy setups (tests, the InMemory database, a missing connection string): there is
/// nobody to coordinate with, so jobs start right away, exactly as before the lock existed.
/// </summary>
public sealed class AlwaysHeldBackgroundJobLock : IBackgroundJobLock
{
  public bool IsHeld => true;

  public Task WaitUntilHeldAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Holds a Postgres session-level advisory lock on a dedicated, unpooled connection.
/// Postgres releases it the moment that connection closes, including when the container is
/// stopped or killed, so the waiting copy picks it up within <see cref="RetryInterval"/>.
/// </summary>
public sealed class PostgresBackgroundJobLock : BackgroundService, IBackgroundJobLock
{
  /// <summary>Arbitrary but fixed: every copy of the API must use the same key.</summary>
  public const long LockKey = 0x4275_6F79_4A6F_6273; // "BuoyJobs"

  private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
  // Short on purpose: if the holder's connection drops while another copy is waiting, the
  // waiter takes the lock within RetryInterval, and the old holder only finds out (and stops
  // itself) on its next heartbeat. Measured: a 15 s heartbeat let both run jobs for ~14 s.
  private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

  private readonly string _connectionString;
  private readonly IHostApplicationLifetime _lifetime;
  private readonly ILogger<PostgresBackgroundJobLock> _logger;
  private readonly TaskCompletionSource _held =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
  private volatile bool _isHeld;

  public PostgresBackgroundJobLock(
    string connectionString,
    IHostApplicationLifetime lifetime,
    ILogger<PostgresBackgroundJobLock> logger)
  {
    // Unpooled: the lock lives as long as this one physical connection, and must never be
    // handed back to the pool (and so to an unrelated query) while held.
    _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
    {
      Pooling = false,
      ApplicationName = "buoyai-job-lock",
    }.ConnectionString;
    _lifetime = lifetime;
    _logger = logger;
  }

  public bool IsHeld => _isHeld;

  public Task WaitUntilHeldAsync(CancellationToken cancellationToken) =>
    _held.Task.WaitAsync(cancellationToken);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    // Never hold up app startup: the new copy has to start serving its health check while
    // the old copy still holds the lock.
    await Task.Yield();

    var announcedWaiting = false;
    while (!stoppingToken.IsCancellationRequested)
    {
      NpgsqlConnection? connection = null;
      try
      {
        connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(stoppingToken);

        if (!await TryLockAsync(connection, stoppingToken))
        {
          if (_isHeld)
          {
            // We held it, lost the connection, and another copy took it in between. Both
            // copies may now be running jobs; stopping this one is the only safe way out.
            _logger.LogCritical(
              "BackgroundJobs: lost the job lock to another copy; stopping this copy so jobs don't run twice");
            _isHeld = false;
            _lifetime.StopApplication();
            return;
          }

          if (!announcedWaiting)
          {
            _logger.LogInformation(
              "BackgroundJobs: another copy holds the job lock; waiting to take over scheduled jobs");
            announcedWaiting = true;
          }

          await connection.DisposeAsync();
          connection = null;
          await Task.Delay(RetryInterval, stoppingToken);
          continue;
        }

        if (!_isHeld)
        {
          _logger.LogInformation(
            "BackgroundJobs: job lock acquired; this copy runs the scheduled jobs");
        }
        _isHeld = true;
        _held.TrySetResult();

        // Hold the lock: a cheap round trip notices a dropped connection, after which the
        // outer loop reconnects and re-takes the lock (nobody else can have it unless the
        // connection really dropped).
        while (!stoppingToken.IsCancellationRequested)
        {
          await Task.Delay(HeartbeatInterval, stoppingToken);
          await using var ping = new NpgsqlCommand("SELECT 1", connection);
          await ping.ExecuteScalarAsync(stoppingToken);
        }
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(
          ex,
          "BackgroundJobs: job lock connection failed (held: {Held}); retrying in {Delay}s",
          _isHeld,
          RetryInterval.TotalSeconds);
        try
        {
          await Task.Delay(RetryInterval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
          break;
        }
      }
      finally
      {
        if (connection is not null)
        {
          await connection.DisposeAsync();
        }
      }
    }

    // Closing the connection (finally above) is what releases the lock.
    _isHeld = false;
  }

  private static async Task<bool> TryLockAsync(NpgsqlConnection connection, CancellationToken ct)
  {
    await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
    cmd.Parameters.AddWithValue("key", LockKey);
    return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
  }
}

/// <summary>
/// Starts the wrapped hosted service only once this copy holds the job lock, and stops it on
/// shutdown. Startup never blocks on the lock, so a waiting copy still serves requests.
/// </summary>
public sealed class JobLockGatedHostedService<TService> : IHostedService, IDisposable
  where TService : class, IHostedService
{
  private readonly TService _inner;
  private readonly IBackgroundJobLock _jobLock;
  private readonly IHostApplicationLifetime _lifetime;
  private readonly ILogger<JobLockGatedHostedService<TService>> _logger;
  private readonly CancellationTokenSource _stopping = new();
  private Task? _waitAndStart;
  private volatile bool _innerStarted;

  public JobLockGatedHostedService(
    TService inner,
    IBackgroundJobLock jobLock,
    IHostApplicationLifetime lifetime,
    ILogger<JobLockGatedHostedService<TService>> logger)
  {
    _inner = inner;
    _jobLock = jobLock;
    _lifetime = lifetime;
    _logger = logger;
  }

  public TService Inner => _inner;

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _waitAndStart = WaitAndStartAsync(_stopping.Token);
    return Task.CompletedTask;
  }

  private async Task WaitAndStartAsync(CancellationToken stoppingToken)
  {
    try
    {
      await _jobLock.WaitUntilHeldAsync(stoppingToken);
      await _inner.StartAsync(stoppingToken);
      _innerStarted = true;

      // The host only watches BackgroundServices it started itself. Mirror its default
      // (BackgroundServiceExceptionBehavior.StopHost) for the ones we start, so a crashed
      // job still takes the copy down instead of failing silently.
      if (_inner is BackgroundService background && background.ExecuteTask is { } execute)
      {
        try
        {
          await execute;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          // normal shutdown
        }
        catch (Exception ex)
        {
          _logger.LogCritical(
            ex,
            "BackgroundService {Service} failed; stopping the host",
            typeof(TService).Name);
          _lifetime.StopApplication();
        }
      }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // Shut down before this copy ever got the lock: the job never started.
    }
  }

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    _stopping.Cancel();
    if (_innerStarted)
    {
      await _inner.StopAsync(cancellationToken);
    }
    if (_waitAndStart is not null)
    {
      try
      {
        await _waitAndStart.WaitAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        // Host shutdown timeout reached; the host logs it.
      }
    }
  }

  // The wrapped service is a DI singleton, so the container disposes it. Like BackgroundService,
  // cancel rather than dispose the source: the host can call StopAsync again after disposal
  // (when a job stops the app, app.Run() stops and disposes the host, and WebApplicationFactory
  // stops it once more), and Cancel on a disposed source throws. It holds no timer, so there is
  // nothing to release.
  public void Dispose() => _stopping.Cancel();
}

public static class BackgroundJobLockServiceCollectionExtensions
{
  /// <summary>
  /// Registers the job lock. Postgres-backed when the API has a real database, otherwise a
  /// lock that is always held (tests, InMemory), which keeps today's single-copy behavior.
  /// </summary>
  public static IServiceCollection AddBackgroundJobLock(
    this IServiceCollection services,
    string? connectionString)
  {
    if (string.IsNullOrWhiteSpace(connectionString))
    {
      services.AddSingleton<IBackgroundJobLock, AlwaysHeldBackgroundJobLock>();
      return services;
    }

    services.AddSingleton(sp => new PostgresBackgroundJobLock(
      connectionString,
      sp.GetRequiredService<IHostApplicationLifetime>(),
      sp.GetRequiredService<ILogger<PostgresBackgroundJobLock>>()));
    services.AddSingleton<IBackgroundJobLock>(sp =>
      sp.GetRequiredService<PostgresBackgroundJobLock>());
    services.AddHostedService(sp => sp.GetRequiredService<PostgresBackgroundJobLock>());
    return services;
  }

  /// <summary>
  /// Like <c>AddHostedService&lt;T&gt;()</c>, but the service only runs in the copy holding the
  /// job lock. Use it for scheduled or database-scanning jobs. Per-request in-memory queue
  /// consumers don't need it: their work only exists in the copy that took the request.
  /// </summary>
  public static IServiceCollection AddJobLockedHostedService<TService>(
    this IServiceCollection services)
    where TService : class, IHostedService
  {
    services.AddSingleton<TService>();
    services.AddHostedService(sp => new JobLockGatedHostedService<TService>(
      sp.GetRequiredService<TService>(),
      sp.GetRequiredService<IBackgroundJobLock>(),
      sp.GetRequiredService<IHostApplicationLifetime>(),
      sp.GetRequiredService<ILogger<JobLockGatedHostedService<TService>>>()));
    return services;
  }
}
