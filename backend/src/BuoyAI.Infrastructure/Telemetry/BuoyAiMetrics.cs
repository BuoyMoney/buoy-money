using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BuoyAI.Infrastructure.Telemetry;

/// <summary>
/// Custom metrics for BuoyAI business operations.
/// These metrics are exported via OpenTelemetry to Grafana Cloud.
/// </summary>
public class BuoyAiMetrics
{
    public const string MeterName = "BuoyAI";
    
    private readonly Meter _meter;
    
    // Authentication metrics
    private readonly Counter<long> _loginAttempts;
    private readonly Counter<long> _loginSuccesses;
    private readonly Counter<long> _loginFailures;
    private readonly Counter<long> _signups;
    private readonly Counter<long> _mfaChallenges;
    private readonly Counter<long> _passwordResets;
    private readonly Counter<long> _accountDeletions;
    
    // Plaid metrics
    private readonly Counter<long> _plaidLinkStarted;
    private readonly Counter<long> _plaidLinkCompleted;
    private readonly Counter<long> _plaidLinkFailed;
    private readonly Counter<long> _plaidSyncs;
    private readonly Counter<long> _plaidTransactionsImported;
    private readonly Histogram<double> _plaidSyncDuration;
    
    // Stripe/Billing metrics
    private readonly Counter<long> _subscriptionCreated;
    private readonly Counter<long> _subscriptionCancelled;
    private readonly Counter<long> _subscriptionUpgraded;
    private readonly Counter<long> _paymentSucceeded;
    private readonly Counter<long> _paymentFailed;
    private readonly Counter<long> _webhookReceived;
    private readonly Counter<long> _webhookFailed;

    // Email delivery metrics
    private readonly Counter<long> _emailSent;

    // Feature usage metrics
    private readonly Counter<long> _budgetsCreated;
    private readonly Counter<long> _savingsGoalsCreated;
    private readonly Counter<long> _familyInvitesSent;
    private readonly Counter<long> _aiConversations;
    private readonly Counter<double> _aiCost;
    private readonly Counter<long> _aiTokens;
    
    // Active gauges
    private int _activeUsers;
    private int _activePlaidConnections;

    // Business snapshot gauges (refreshed by BusinessMetricsService; int reads/writes
    // are atomic in .NET, matching the _activeUsers pattern above). MRR is whole-dollar
    // (tier prices are integers), so an int avoids torn double reads.
    private int _mrrDollars;
    private int _payingStandard;
    private int _payingPremium;
    private int _payingAdmin;
    private int _activeTrials;
    private int _churnedUsers;

    // Background job metrics
    private readonly Counter<long> _backgroundJobRuns;
    private readonly Counter<long> _backgroundJobSuccesses;
    private readonly Counter<long> _backgroundJobFailures;
    private readonly Histogram<double> _backgroundJobDuration;
    private readonly Counter<long> _subscriptionsDetected;
    private readonly Counter<long> _unhealthyBudgets;

    public BuoyAiMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        
        // Authentication
        _loginAttempts = _meter.CreateCounter<long>(
            "buoyai.auth.login.attempts",
            description: "Total login attempts");
        
        _loginSuccesses = _meter.CreateCounter<long>(
            "buoyai.auth.login.successes",
            description: "Successful logins");
        
        _loginFailures = _meter.CreateCounter<long>(
            "buoyai.auth.login.failures",
            description: "Failed logins");
        
        _signups = _meter.CreateCounter<long>(
            "buoyai.auth.signups",
            description: "New user registrations");
        
        _mfaChallenges = _meter.CreateCounter<long>(
            "buoyai.auth.mfa.challenges",
            description: "MFA challenges issued");
        
        _passwordResets = _meter.CreateCounter<long>(
            "buoyai.auth.password_resets",
            description: "Password reset requests");
        
        _accountDeletions = _meter.CreateCounter<long>(
            "buoyai.auth.account_deletions",
            description: "Accounts permanently deleted");
        
        // Plaid
        _plaidLinkStarted = _meter.CreateCounter<long>(
            "buoyai.plaid.link.started",
            description: "Plaid Link sessions started");
        
        _plaidLinkCompleted = _meter.CreateCounter<long>(
            "buoyai.plaid.link.completed",
            description: "Plaid Link sessions completed successfully");
        
        _plaidLinkFailed = _meter.CreateCounter<long>(
            "buoyai.plaid.link.failed",
            description: "Plaid Link sessions that failed");
        
        _plaidSyncs = _meter.CreateCounter<long>(
            "buoyai.plaid.syncs",
            description: "Plaid transaction sync operations");
        
        _plaidTransactionsImported = _meter.CreateCounter<long>(
            "buoyai.plaid.transactions.imported",
            description: "Transactions imported from Plaid");
        
        _plaidSyncDuration = _meter.CreateHistogram<double>(
            "buoyai.plaid.sync.duration",
            unit: "s",
            description: "Plaid sync operation duration in seconds");
        
        // Billing
        _subscriptionCreated = _meter.CreateCounter<long>(
            "buoyai.billing.subscription.created",
            description: "New subscriptions created");
        
        _subscriptionCancelled = _meter.CreateCounter<long>(
            "buoyai.billing.subscription.cancelled",
            description: "Subscriptions cancelled");
        
        _subscriptionUpgraded = _meter.CreateCounter<long>(
            "buoyai.billing.subscription.upgraded",
            description: "Subscriptions upgraded");
        
        _paymentSucceeded = _meter.CreateCounter<long>(
            "buoyai.billing.payment.succeeded",
            description: "Successful payments");
        
        _paymentFailed = _meter.CreateCounter<long>(
            "buoyai.billing.payment.failed",
            description: "Failed payments");

        _webhookReceived = _meter.CreateCounter<long>(
            "buoyai.billing.webhook.received",
            description: "Stripe webhook events received (after signature verification)");

        _webhookFailed = _meter.CreateCounter<long>(
            "buoyai.billing.webhook.failed",
            description: "Stripe webhook events that failed signature verification or handling");

        // Email delivery
        _emailSent = _meter.CreateCounter<long>(
            "buoyai.email.sent",
            description: "Emails dispatched via the email provider, tagged by template and outcome");

        // Feature usage
        _budgetsCreated = _meter.CreateCounter<long>(
            "buoyai.feature.budgets.created",
            description: "Budgets created");
        
        _savingsGoalsCreated = _meter.CreateCounter<long>(
            "buoyai.feature.savings_goals.created",
            description: "Savings goals created");
        
        _familyInvitesSent = _meter.CreateCounter<long>(
            "buoyai.feature.family.invites_sent",
            description: "Family invitations sent");
        
        _aiConversations = _meter.CreateCounter<long>(
            "buoyai.feature.ai.conversations",
            description: "AI chat conversations");

        // Gemini spend, for a RATE alert rather than a balance alert.
        //
        // Gemini moves to prepay on 2026-10-12: credits are bought up front and the API
        // stops when they run out. Google exposes no remaining-balance API we can read,
        // so the useful signal is our own spend rate. At current volume (~$0.05/month,
        // ~$0.11 lifetime) a slow drain is not the threat — a runaway loop hammering the
        // API is, and that shows up as a cost-per-hour spike long before the balance is
        // gone. Alert on the spike, not on "credits low".
        //
        // No unit annotation, deliberately: Grafana Cloud appends unit suffixes for
        // non-UCUM units like "USD", which would mangle the exported metric name. This
        // exports as `buoyai_ai_cost_total` and is formatted as currency on the panel,
        // matching the buoyai.business.mrr gauge above.
        _aiCost = _meter.CreateCounter<double>(
            "buoyai.ai.cost",
            description: "Estimated AI model spend in USD");

        _aiTokens = _meter.CreateCounter<long>(
            "buoyai.ai.tokens",
            description: "AI model tokens consumed");
        
        // Active users gauge
        _meter.CreateObservableGauge(
            "buoyai.users.active",
            () => _activeUsers,
            description: "Currently active users (last 5 min)");
        
        _meter.CreateObservableGauge(
            "buoyai.plaid.connections.active",
            () => _activePlaidConnections,
            description: "Active Plaid bank connections");

        // Business snapshot gauges — revenue + subscriber KPIs for the Grafana "Goals"
        // dashboard (#811). Backed by fields refreshed periodically by BusinessMetricsService.
        // No unit annotation: keeps the exported Prometheus name a clean
        // `buoyai_business_mrr` (Grafana Cloud appends unit suffixes for non-UCUM
        // units like "USD"). USD is formatted on the Grafana panel instead.
        _meter.CreateObservableGauge(
            "buoyai.business.mrr",
            () => _mrrDollars,
            description: "Monthly recurring revenue in USD (Standard $10 + Premium $15 + Admin $50)");

        _meter.CreateObservableGauge(
            "buoyai.business.paying_users",
            () => new[]
            {
                new Measurement<int>(_payingStandard, new KeyValuePair<string, object?>("tier", "standard")),
                new Measurement<int>(_payingPremium, new KeyValuePair<string, object?>("tier", "premium")),
                new Measurement<int>(_payingAdmin, new KeyValuePair<string, object?>("tier", "admin")),
            },
            description: "Current paying users by subscription tier");

        _meter.CreateObservableGauge(
            "buoyai.business.active_trials",
            () => _activeTrials,
            description: "Users currently in an active trial");

        _meter.CreateObservableGauge(
            "buoyai.business.churned_users",
            () => _churnedUsers,
            description: "Users who used a trial but hold no subscription");

        // Background jobs
        _backgroundJobRuns = _meter.CreateCounter<long>(
            "buoyai.jobs.runs",
            description: "Background job executions");
        
        _backgroundJobSuccesses = _meter.CreateCounter<long>(
            "buoyai.jobs.successes",
            description: "Successful background job completions");
        
        _backgroundJobFailures = _meter.CreateCounter<long>(
            "buoyai.jobs.failures",
            description: "Failed background job executions");
        
        _backgroundJobDuration = _meter.CreateHistogram<double>(
            "buoyai.jobs.duration",
            unit: "s",
            description: "Background job execution duration in seconds");
        
        _subscriptionsDetected = _meter.CreateCounter<long>(
            "buoyai.jobs.subscriptions_detected",
            description: "Expense subscriptions detected by detection service");
        
        _unhealthyBudgets = _meter.CreateCounter<long>(
            "buoyai.jobs.unhealthy_budgets",
            description: "Budgets flagged as unhealthy by health check service");
    }
    
    // Authentication methods
    public void RecordLoginAttempt() => _loginAttempts.Add(1);
    public void RecordLoginSuccess() => _loginSuccesses.Add(1);
    public void RecordLoginFailure(string reason) => _loginFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void RecordSignup() => _signups.Add(1);
    public void RecordMfaChallenge(string method) => _mfaChallenges.Add(1, new KeyValuePair<string, object?>("method", method));
    public void RecordPasswordReset() => _passwordResets.Add(1);
    public void RecordAccountDeleted() => _accountDeletions.Add(1);
    
    // Plaid methods
    public void RecordPlaidLinkStarted() => _plaidLinkStarted.Add(1);
    public void RecordPlaidLinkCompleted() => _plaidLinkCompleted.Add(1);
    public void RecordPlaidLinkFailed(string errorCode) => _plaidLinkFailed.Add(1, new KeyValuePair<string, object?>("error_code", errorCode));
    public void RecordPlaidSync(int transactionCount) 
    {
        _plaidSyncs.Add(1);
        _plaidTransactionsImported.Add(transactionCount);
    }
    public void RecordPlaidSyncDuration(double durationSeconds) => _plaidSyncDuration.Record(durationSeconds);
    
    // Billing methods
    public void RecordSubscriptionCreated(string plan) => _subscriptionCreated.Add(1, new KeyValuePair<string, object?>("plan", plan));
    public void RecordSubscriptionCancelled(string plan) => _subscriptionCancelled.Add(1, new KeyValuePair<string, object?>("plan", plan));
    public void RecordSubscriptionUpgraded(string fromPlan, string toPlan) => _subscriptionUpgraded.Add(1, 
        new KeyValuePair<string, object?>("from_plan", fromPlan),
        new KeyValuePair<string, object?>("to_plan", toPlan));
    public void RecordPaymentSucceeded(decimal amount) => _paymentSucceeded.Add(1, new KeyValuePair<string, object?>("amount_bucket", GetAmountBucket(amount)));
    public void RecordPaymentFailed(string reason) => _paymentFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void RecordWebhookReceived(string eventType) => _webhookReceived.Add(1, new KeyValuePair<string, object?>("event_type", eventType));
    public void RecordWebhookFailed(string stage, string eventType) => _webhookFailed.Add(1,
        new KeyValuePair<string, object?>("stage", stage),
        new KeyValuePair<string, object?>("event_type", eventType));

    // Email delivery methods
    public void RecordEmailSent(string template, string outcome, string? errorType = null)
    {
        if (errorType == null)
        {
            _emailSent.Add(1,
                new KeyValuePair<string, object?>("template", template),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
        else
        {
            _emailSent.Add(1,
                new KeyValuePair<string, object?>("template", template),
                new KeyValuePair<string, object?>("outcome", outcome),
                new KeyValuePair<string, object?>("error_type", errorType));
        }
    }
    
    // Trial methods (card-upfront flow)
    public void RecordTrialStarted(string plan) => _subscriptionCreated.Add(1, 
        new KeyValuePair<string, object?>("plan", plan),
        new KeyValuePair<string, object?>("trial", "true"));
    public void RecordTrialConverted(string plan) => _subscriptionCreated.Add(1, 
        new KeyValuePair<string, object?>("plan", plan),
        new KeyValuePair<string, object?>("trial_converted", "true"));
    
    // Feature usage methods
    public void RecordBudgetCreated() => _budgetsCreated.Add(1);
    public void RecordSavingsGoalCreated() => _savingsGoalsCreated.Add(1);
    public void RecordFamilyInviteSent() => _familyInvitesSent.Add(1);
    public void RecordAiConversation() => _aiConversations.Add(1);

    /// <summary>
    /// Records estimated spend and tokens for one model call. Labelled by model and
    /// context so a spike can be attributed (chat vs insights vs embeddings) instead of
    /// just showing that something is burning credits.
    /// </summary>
    public void RecordAiUsage(string model, string context, long tokens, decimal cost)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("context", context)
        };
        _aiCost.Add((double)cost, tags);
        _aiTokens.Add(tokens, tags);
    }
    
    // Gauge setters (called periodically)
    public void SetActiveUsers(int count) => _activeUsers = count;
    public void SetActivePlaidConnections(int count) => _activePlaidConnections = count;

    /// <summary>Refresh the business snapshot gauges (MRR, paying users by tier, trials, churn).</summary>
    public void SetBusinessSnapshot(int mrrDollars, int payingStandard, int payingPremium, int payingAdmin, int activeTrials, int churnedUsers)
    {
        _mrrDollars = mrrDollars;
        _payingStandard = payingStandard;
        _payingPremium = payingPremium;
        _payingAdmin = payingAdmin;
        _activeTrials = activeTrials;
        _churnedUsers = churnedUsers;
    }
    
    // Background job methods
    public void RecordBackgroundJobRun(string jobName) => _backgroundJobRuns.Add(1, new KeyValuePair<string, object?>("job", jobName));
    public void RecordBackgroundJobSuccess(string jobName) => _backgroundJobSuccesses.Add(1, new KeyValuePair<string, object?>("job", jobName));
    public void RecordBackgroundJobFailure(string jobName, string error) => _backgroundJobFailures.Add(1, 
        new KeyValuePair<string, object?>("job", jobName),
        new KeyValuePair<string, object?>("error", error));
    public void RecordBackgroundJobDuration(string jobName, double durationSeconds) => _backgroundJobDuration.Record(durationSeconds, 
        new KeyValuePair<string, object?>("job", jobName));
    public void RecordSubscriptionsDetected(int count) => _subscriptionsDetected.Add(count);
    public void RecordUnhealthyBudgets(int count) => _unhealthyBudgets.Add(count);
    
    private static string GetAmountBucket(decimal amount) => amount switch
    {
        < 10 => "0-10",
        < 50 => "10-50",
        < 100 => "50-100",
        _ => "100+"
    };
}

/// <summary>
/// ActivitySource for custom distributed tracing.
/// </summary>
public static class BuoyAiActivitySource
{
    public const string Name = "BuoyAI";
    public static readonly ActivitySource Instance = new(Name, "1.0.0");
    
    public static Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return Instance.StartActivity(name, kind);
    }
}
