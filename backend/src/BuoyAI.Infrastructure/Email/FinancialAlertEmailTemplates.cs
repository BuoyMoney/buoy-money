namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Email templates for budget alerts, savings milestones, and financial notifications.
/// </summary>
public static class FinancialAlertEmailTemplates
{
    public static (string Html, string Text) BudgetAlert(
        string firstName,
        string budgetName,
        decimal spent,
        decimal limit,
        int percentUsed,
        string alertType)
    {
        var alertTitle = alertType.ToLowerInvariant() switch
        {
            "warning" => "Budget Warning ⚠️",
            "exceeded" => "Budget Exceeded 🚨",
            _ => "Budget Update"
        };

        var alertBox = alertType.ToLowerInvariant() switch
        {
            "exceeded" => EmailTemplateBase.DangerBox($"<strong>You've exceeded your {budgetName} budget!</strong>"),
            "warning" => EmailTemplateBase.WarningBox($"<strong>You've used {percentUsed}% of your {budgetName} budget.</strong>"),
            _ => EmailTemplateBase.InfoBox($"<strong>Your {budgetName} budget is at {percentUsed}%.</strong>")
        };

        var remaining = limit - spent;
        var remainingText = remaining >= 0
            ? $"<strong>Remaining:</strong> ${remaining:F2}"
            : $"<strong>Over budget by:</strong> ${Math.Abs(remaining):F2}";

        // Simple progress bar
        var progressPercent = Math.Min(percentUsed, 100);
        var progressColor = percentUsed >= 100 ? "#EF4444" : percentUsed >= 80 ? "#F59E0B" : "#10B981";
        var progressBar = $@"
            <div style='background: #E5E7EB; border-radius: 4px; height: 12px; margin: 16px 0;'>
                <div style='background: {progressColor}; width: {progressPercent}%; height: 100%; border-radius: 4px;'></div>
            </div>";

        var content = $@"
        {EmailTemplateBase.SectionHeading(alertTitle)}
        
        <p>Hi {firstName},</p>
        
        {alertBox}
        
        <div style='margin: 20px 0;'>
            <p style='margin: 4px 0;'><strong>Spent:</strong> ${spent:F2} of ${limit:F2}</p>
            <p style='margin: 4px 0;'>{remainingText}</p>
            {progressBar}
        </div>
        
        <p>View your budget details and recent transactions:</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("View Budget", "https://buoy-ai.com/app/budgets")}
        </div>
        
        <p style='color: #6B7280; font-size: 14px;'>
            💡 <em>Tip: Review your recent spending to identify areas where you can cut back.</em>
        </p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Budget Alert", "💰", content, "Stay on track!<br>The Buoy Money Team");

        var text = $@"{alertTitle}

Hi {firstName},

{alertType.ToLowerInvariant() switch
{
    "exceeded" => $"You've exceeded your {budgetName} budget!",
    "warning" => $"You've used {percentUsed}% of your {budgetName} budget.",
    _ => $"Your {budgetName} budget is at {percentUsed}%."
}}

Spent: ${spent:F2} of ${limit:F2}
{(remaining >= 0 ? $"Remaining: ${remaining:F2}" : $"Over budget by: ${Math.Abs(remaining):F2}")}

View budget: https://buoy-ai.com/app/budgets

Stay on track!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) SavingsGoalMilestone(
        string firstName,
        string goalName,
        decimal currentAmount,
        decimal targetAmount,
        int percentComplete,
        string milestoneType)
    {
        var milestoneTitle = milestoneType.ToLowerInvariant() switch
        {
            "complete" => "🎉 Goal Achieved!",
            "halfway" => "🎯 Halfway There!",
            "started" => "🚀 Great Start!",
            _ => "📈 Savings Update"
        };

        var milestoneMessage = milestoneType.ToLowerInvariant() switch
        {
            "complete" => $"Congratulations! You've reached your <strong>{goalName}</strong> goal!",
            "halfway" => $"Amazing progress! You're halfway to your <strong>{goalName}</strong> goal!",
            "started" => $"You've made your first contribution to <strong>{goalName}</strong>!",
            _ => $"You've made progress on your <strong>{goalName}</strong> goal!"
        };

        var successBox = EmailTemplateBase.SuccessBox(milestoneMessage);

        // Progress bar
        var progressPercent = Math.Min(percentComplete, 100);
        var progressBar = $@"
            <div style='background: #E5E7EB; border-radius: 4px; height: 12px; margin: 16px 0;'>
                <div style='background: #10B981; width: {progressPercent}%; height: 100%; border-radius: 4px;'></div>
            </div>";

        var remainingAmount = targetAmount - currentAmount;
        var remainingSection = remainingAmount > 0
            ? $"<p><strong>Remaining:</strong> ${remainingAmount:F2}</p>"
            : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading(milestoneTitle)}
        
        <p>Hi {firstName},</p>
        
        {successBox}
        
        <div style='margin: 20px 0;'>
            <p style='margin: 4px 0;'><strong>Saved:</strong> ${currentAmount:F2}</p>
            <p style='margin: 4px 0;'><strong>Target:</strong> ${targetAmount:F2}</p>
            {remainingSection}
            {progressBar}
            <p style='text-align: center; color: #10B981; font-weight: bold;'>{percentComplete}% Complete</p>
        </div>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("View Savings", "https://buoy-ai.com/app/savings")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Savings", "🎯", content, "Keep up the great work!<br>The Buoy Money Team");

        var text = $@"{milestoneTitle}

Hi {firstName},

{milestoneType.ToLowerInvariant() switch
{
    "complete" => $"Congratulations! You've reached your {goalName} goal!",
    "halfway" => $"Amazing progress! You're halfway to your {goalName} goal!",
    "started" => $"You've made your first contribution to {goalName}!",
    _ => $"You've made progress on your {goalName} goal!"
}}

Saved: ${currentAmount:F2} of ${targetAmount:F2}
Progress: {percentComplete}%

View savings: https://buoy-ai.com/app/savings

Keep up the great work!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) LargeTransactionAlert(
        string firstName,
        string merchantName,
        decimal amount,
        string accountName,
        DateTime transactionDate,
        string category)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Large Transaction Detected")}
        
        <p>Hi {firstName},</p>
        
        <p>We noticed an unusually large transaction on your account:</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Amount:</strong> ${amount:F2}<br>
            <strong>Merchant:</strong> {merchantName}<br>
            <strong>Account:</strong> {accountName}<br>
            <strong>Date:</strong> {transactionDate:MMMM d, yyyy}<br>
            <strong>Category:</strong> {category}
        ")}
        
        <p>If this transaction looks correct, no action is needed. If you don't recognize this transaction, please review your account immediately.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Review Transactions", "https://buoy-ai.com/app/transactions")}
        </div>
        
        <p style='color: #6B7280; font-size: 14px;'>
            You can adjust your large transaction alert threshold in your notification settings.
        </p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Alert", "💸", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Large Transaction Detected

Hi {firstName},

We noticed an unusually large transaction on your account:

Amount: ${amount:F2}
Merchant: {merchantName}
Account: {accountName}
Date: {transactionDate:MMMM d, yyyy}
Category: {category}

Review transactions: https://buoy-ai.com/app/transactions

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) MonthlySummary(
        string firstName,
        string monthName,
        decimal totalIncome,
        decimal totalSpending,
        decimal netSavings,
        string topCategory,
        decimal topCategoryAmount,
        int budgetsOnTrack,
        int totalBudgets)
    {
        var savingsStatus = netSavings >= 0
            ? EmailTemplateBase.SuccessBox($"<strong>Great news!</strong> You saved ${netSavings:F2} this month!")
            : EmailTemplateBase.WarningBox($"<strong>Heads up:</strong> You spent ${Math.Abs(netSavings):F2} more than you earned.");

        var budgetStatus = budgetsOnTrack == totalBudgets
            ? "🎉 All budgets on track!"
            : $"{budgetsOnTrack} of {totalBudgets} budgets on track";

        var content = $@"
        {EmailTemplateBase.SectionHeading($"Your {monthName} Summary")}
        
        <p>Hi {firstName},</p>
        
        <p>Here's how you did financially in {monthName}:</p>
        
        {savingsStatus}
        
        <div style='display: flex; justify-content: space-around; flex-wrap: wrap; margin: 20px 0;'>
            {EmailTemplateBase.StatBox("Income", $"${totalIncome:F2}", "#10B981")}
            {EmailTemplateBase.StatBox("Spending", $"${totalSpending:F2}", "#EF4444")}
            {EmailTemplateBase.StatBox("Net Savings", $"${netSavings:F2}", netSavings >= 0 ? "#10B981" : "#EF4444")}
        </div>
        
        {EmailTemplateBase.Divider()}
        
        <p><strong>Top spending category:</strong> {topCategory} (${topCategoryAmount:F2})</p>
        <p><strong>Budget status:</strong> {budgetStatus}</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("View Full Report", "https://buoy-ai.com/app/reports")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Monthly Summary", "📊", content, "Here's to a great month ahead!<br>The Buoy Money Team");

        var text = $@"Your {monthName} Summary

Hi {firstName},

Here's how you did financially in {monthName}:

Income: ${totalIncome:F2}
Spending: ${totalSpending:F2}
Net Savings: ${netSavings:F2}

Top spending category: {topCategory} (${topCategoryAmount:F2})
Budget status: {budgetStatus}

View full report: https://buoy-ai.com/app/reports

Here's to a great month ahead!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) PlaidConnectionAlert(
        string firstName,
        string accountName,
        string institutionName,
        string alertType,
        string? errorMessage)
    {
        var alertTitle = alertType.ToLowerInvariant() switch
        {
            "disconnected" => "Bank Connection Lost",
            "requires_attention" => "Bank Connection Needs Attention",
            "reconnected" => "Bank Connection Restored! ✓",
            _ => "Bank Connection Update"
        };

        var alertBox = alertType.ToLowerInvariant() switch
        {
            "disconnected" => EmailTemplateBase.DangerBox($"Your connection to <strong>{institutionName}</strong> has been disconnected."),
            "requires_attention" => EmailTemplateBase.WarningBox($"Your <strong>{institutionName}</strong> connection requires your attention."),
            "reconnected" => EmailTemplateBase.SuccessBox($"Your connection to <strong>{institutionName}</strong> has been restored."),
            _ => EmailTemplateBase.InfoBox($"There's an update to your <strong>{institutionName}</strong> connection.")
        };

        var errorSection = !string.IsNullOrEmpty(errorMessage)
            ? $"<p style='color: #6B7280; font-size: 14px;'>Error details: {errorMessage}</p>"
            : "";

        var actionSection = alertType.ToLowerInvariant() != "reconnected"
            ? $@"
            <p>To continue syncing your transactions, please reconnect your account:</p>
            <div style='text-align: center; margin: 30px 0;'>
                {EmailTemplateBase.PrimaryButton("Reconnect Account", "https://buoy-ai.com/app/accounts")}
            </div>"
            : $@"
            <p>Your transactions will now sync automatically. No action is needed.</p>
            <div style='text-align: center; margin: 30px 0;'>
                {EmailTemplateBase.SecondaryButton("View Accounts", "https://buoy-ai.com/app/accounts")}
            </div>";

        var content = $@"
        {EmailTemplateBase.SectionHeading(alertTitle)}
        
        <p>Hi {firstName},</p>
        
        {alertBox}
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Account:</strong> {accountName}<br>
            <strong>Institution:</strong> {institutionName}
        ")}
        
        {errorSection}
        
        {actionSection}";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "🏦", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"{alertTitle}

Hi {firstName},

{alertType.ToLowerInvariant() switch
{
    "disconnected" => $"Your connection to {institutionName} has been disconnected.",
    "requires_attention" => $"Your {institutionName} connection requires your attention.",
    "reconnected" => $"Your connection to {institutionName} has been restored.",
    _ => $"There's an update to your {institutionName} connection."
}}

Account: {accountName}
Institution: {institutionName}
{(!string.IsNullOrEmpty(errorMessage) ? $"\nError: {errorMessage}" : "")}

{(alertType.ToLowerInvariant() != "reconnected" ? "Reconnect account: https://buoy-ai.com/app/accounts" : "View accounts: https://buoy-ai.com/app/accounts")}

Best regards,
The Buoy Money Team";

        return (html, text);
    }
}
