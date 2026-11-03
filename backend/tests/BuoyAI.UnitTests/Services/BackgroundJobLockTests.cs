using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Blue-green prod deploys run two copies of the API for about a minute. These lock the
/// contract that keeps scheduled jobs from running twice: a gated job starts only once its
/// copy holds the job lock, never starts in a copy that shuts down while still waiting, and
/// startup never blocks on the lock (the new copy must serve its health check meanwhile).
/// </summary>
public class BackgroundJobLockTests
{
  private sealed class ManualJobLock : IBackgroundJobLock
  {
    private readonly TaskCompletionSource _held =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsHeld => _held.Task.IsCompleted;

    public Task WaitUntilHeldAsync(CancellationToken cancellationToken) =>
      _held.Task.WaitAsync(cancellationToken);

    public void Grant() => _held.TrySetResult();
  }

  private sealed class RecordingJob : BackgroundService
  {
    public readonly TaskCompletionSource Started =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Stopped { get; private set; }
    public Exception? FailWith { get; init; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      Started.TrySetResult();
      if (FailWith is not null)
      {
        await Task.Yield();
        throw FailWith;
      }
      try
      {
        await Task.Delay(Timeout.Infinite, stoppingToken);
      }
      finally
      {
        Stopped = true;
      }
    }
  }

  private static JobLockGatedHostedService<RecordingJob> Gate(
    RecordingJob job,
    IBackgroundJobLock jobLock,
    IHostApplicationLifetime? lifetime = null) =>
    new(
      job,
      jobLock,
      lifetime ?? Mock.Of<IHostApplicationLifetime>(),
      NullLogger<JobLockGatedHostedService<RecordingJob>>.Instance);

  [Fact]
  public async Task GatedJob_StartsOnlyOnceTheLockIsHeld()
  {
    var jobLock = new ManualJobLock();
    var job = new RecordingJob();
    var gate = Gate(job, jobLock);

    var start = gate.StartAsync(CancellationToken.None);

    start.IsCompleted.Should().BeTrue("app startup must not wait for the job lock");
    await Task.Delay(100);
    job.Started.Task.IsCompleted.Should().BeFalse("the other copy still holds the lock");

    jobLock.Grant();
    await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await gate.StopAsync(CancellationToken.None);
    job.Stopped.Should().BeTrue();
  }

  [Fact]
  public async Task GatedJob_NeverStarts_WhenTheCopyStopsWhileWaiting()
  {
    var jobLock = new ManualJobLock();
    var job = new RecordingJob();
    var gate = Gate(job, jobLock);

    await gate.StartAsync(CancellationToken.None);
    await gate.StopAsync(CancellationToken.None);
    jobLock.Grant();
    await Task.Delay(100);

    job.Started.Task.IsCompleted.Should().BeFalse();
  }

  [Fact]
  public async Task GatedJob_StartsImmediately_WithTheAlwaysHeldLock()
  {
    var job = new RecordingJob();
    var gate = Gate(job, new AlwaysHeldBackgroundJobLock());

    await gate.StartAsync(CancellationToken.None);
    await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await gate.StopAsync(CancellationToken.None);
  }

  [Fact]
  public async Task GatedJob_StopAfterDispose_DoesNotThrow_LikeABackgroundService()
  {
    // When a job stops the app, app.Run() stops and disposes the host; WebApplicationFactory
    // then stops it again on teardown. A plain BackgroundService shrugs that off; the gate must
    // too, or every gated job fails the second stop (seen in CI as "hosted services failed to
    // stop: the CancellationTokenSource has been disposed").
    var job = new RecordingJob();
    var gate = Gate(job, new AlwaysHeldBackgroundJobLock());
    await gate.StartAsync(CancellationToken.None);
    await job.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await gate.StopAsync(CancellationToken.None);

    gate.Dispose();
    var secondStop = () => gate.StopAsync(CancellationToken.None);

    await secondStop.Should().NotThrowAsync();
  }

  [Fact]
  public async Task GatedJob_Crash_StopsTheHost_LikeAnUngatedBackgroundService()
  {
    var lifetime = new Mock<IHostApplicationLifetime>();
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    lifetime.Setup(l => l.StopApplication()).Callback(() => stopped.TrySetResult());
    var job = new RecordingJob { FailWith = new InvalidOperationException("boom") };
    var gate = Gate(job, new AlwaysHeldBackgroundJobLock(), lifetime.Object);

    await gate.StartAsync(CancellationToken.None);

    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    lifetime.Verify(l => l.StopApplication(), Times.Once);
  }

  [Fact]
  public void AddBackgroundJobLock_WithoutADatabase_UsesTheAlwaysHeldLock()
  {
    var services = new ServiceCollection();

    services.AddBackgroundJobLock(null);

    using var provider = services.BuildServiceProvider();
    provider.GetRequiredService<IBackgroundJobLock>().Should().BeOfType<AlwaysHeldBackgroundJobLock>();
    services.Should().NotContain(d => d.ServiceType == typeof(IHostedService));
  }

  [Fact]
  public void AddBackgroundJobLock_WithADatabase_RegistersThePostgresLockAsAHostedService()
  {
    var services = new ServiceCollection();
    services.AddSingleton(Mock.Of<IHostApplicationLifetime>());
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

    services.AddBackgroundJobLock("Host=localhost;Database=buoyai;Username=u;Password=p");

    using var provider = services.BuildServiceProvider();
    var jobLock = provider.GetRequiredService<IBackgroundJobLock>();
    jobLock.Should().BeOfType<PostgresBackgroundJobLock>();
    jobLock.IsHeld.Should().BeFalse("nothing has run yet");
    provider.GetServices<IHostedService>().Should().ContainSingle()
      .Which.Should().BeSameAs(jobLock);
  }

  [Fact]
  public void AddJobLockedHostedService_RegistersTheGateNotTheRawJob()
  {
    var services = new ServiceCollection();
    services.AddSingleton(Mock.Of<IHostApplicationLifetime>());
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    services.AddBackgroundJobLock(null);

    services.AddJobLockedHostedService<RecordingJob>();

    using var provider = services.BuildServiceProvider();
    provider.GetServices<IHostedService>().Should().ContainSingle()
      .Which.Should().BeOfType<JobLockGatedHostedService<RecordingJob>>()
      .Which.Inner.Should().BeSameAs(provider.GetRequiredService<RecordingJob>());
  }
}
