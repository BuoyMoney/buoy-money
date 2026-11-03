using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Self-healing sweep for trials that were granted locally but never reached Stripe.
///
/// Registration deliberately does not fail when Stripe is unreachable: the account is
/// created and the trial granted from local fields, which is what access checks read. The
/// cost of that choice is drift — a user trialing with no Stripe subscription behind them
/// gets no <c>trial_will_end</c> reminder and no pause at expiry. This job closes that gap
/// so a Stripe outage during signup degrades to "the reminder is late", not "this user
/// silently never converts".
///
/// Runs every 30 minutes. Provisioning is idempotent, so overlapping with a late-succeeding
/// registration attempt is harmless.
/// </summary>
public class TrialReconciliationService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    // Give a registration in flight time to finish before treating it as orphaned.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrialReconciliationService> _logger;

    public TrialReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<TrialReconciliationService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var provisioning = scope.ServiceProvider.GetRequiredService<ITrialProvisioningService>();

                // Half one: trials that never reached Stripe at all.
                var repaired = await provisioning.ReconcileMissingTrialsAsync(ct: stoppingToken);
                if (repaired > 0)
                {
                    _logger.LogInformation("Reconciled {Count} trial(s) onto Stripe", repaired);
                }

                // Half two: trials that DID reach Stripe but may since have diverged, e.g. a
                // `paused` webhook that never arrived. Gap-filling alone could never see these.
                await provisioning.ReconcileDriftedTrialsAsync(ct: stoppingToken);

                // Half three: trials that expired before ever reaching Stripe. Invisible to both
                // halves above, so without this they stay flagged active against a past date
                // forever — entitled to nothing, with no route to a plan.
                var closed = await provisioning.CloseLapsedLocalTrialsAsync(ct: stoppingToken);
                if (closed > 0)
                {
                    _logger.LogInformation("Closed {Count} lapsed local trial(s)", closed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad sweep kill the loop; the next pass retries.
                _logger.LogError(ex, "Trial reconciliation sweep failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
