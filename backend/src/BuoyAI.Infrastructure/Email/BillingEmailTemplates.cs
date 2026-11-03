namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Email templates for subscription and billing-related emails.
/// </summary>
public static class BillingEmailTemplates
{
    public static (string Html, string Text) SubscriptionChanged(
        string firstName,
        string planName,
        string changeType,
        decimal price,
        string billingPeriod,
        DateTime? nextBillingDate)
    {
        var changeTitle = changeType.ToLowerInvariant() switch
        {
            "upgrade" => "Subscription Upgraded! 🎉",
            "downgrade" => "Subscription Changed",
            "cancel" => "Subscription Cancelled",
            "reactivate" => "Subscription Reactivated! 🎉",
            _ => "Subscription Updated"
        };

        var changeMessage = changeType.ToLowerInvariant() switch
        {
            "upgrade" => "Thank you for upgrading! You now have access to all premium features.",
            "downgrade" => "Your subscription has been changed. You'll continue to have access to your current features until the end of your billing period.",
            "cancel" => "We're sorry to see you go. Your subscription will remain active until the end of your current billing period.",
            "reactivate" => "Welcome back! Your subscription has been reactivated and you have full access to all features.",
            _ => "Your subscription has been updated."
        };

        var nextBillingSection = nextBillingDate.HasValue
            ? $"<p><strong>Next billing date:</strong> {nextBillingDate.Value:MMMM d, yyyy}</p>"
            : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading(changeTitle)}
        
        <p>Hi {firstName},</p>
        
        <p>{changeMessage}</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Plan:</strong> {planName}<br>
            <strong>Price:</strong> ${price:F2}/{billingPeriod}
        ")}
        
        {nextBillingSection}
        
        <p>You can manage your subscription anytime from your account settings.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("Manage Subscription", "https://buoy-ai.com/app/settings/billing")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Billing", "💳", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"{changeTitle}

Hi {firstName},

{changeMessage}

Plan: {planName}
Price: ${price:F2}/{billingPeriod}
{(nextBillingDate.HasValue ? $"Next billing date: {nextBillingDate.Value:MMMM d, yyyy}" : "")}

Manage subscription: https://buoy-ai.com/app/settings/billing

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) PaymentFailed(
        string firstName,
        string planName,
        decimal amount,
        string lastFourDigits,
        int retryAttempt,
        DateTime? nextRetryDate)
    {
        var retryMessage = nextRetryDate.HasValue
            ? $"We'll automatically retry the payment on {nextRetryDate.Value:MMMM d, yyyy}."
            : "Please update your payment method to avoid service interruption.";

        var alertType = retryAttempt >= 3
            ? EmailTemplateBase.DangerBox("Your subscription is at risk of cancellation. Please update your payment method immediately.")
            : EmailTemplateBase.WarningBox("We were unable to process your payment. Please update your payment method to avoid service interruption.");

        var content = $@"
        {EmailTemplateBase.SectionHeading("Payment Failed")}
        
        <p>Hi {firstName},</p>
        
        {alertType}
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Plan:</strong> {planName}<br>
            <strong>Amount:</strong> ${amount:F2}<br>
            <strong>Card ending in:</strong> {lastFourDigits}<br>
            <strong>Attempt:</strong> {retryAttempt} of 4
        ")}
        
        <p>{retryMessage}</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Update Payment Method", "https://buoy-ai.com/app/settings/billing")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Billing", "⚠️", content, "Best regards,<br>The Buoy Money Billing Team");

        var text = $@"Payment Failed

Hi {firstName},

We were unable to process your payment.

Plan: {planName}
Amount: ${amount:F2}
Card ending in: {lastFourDigits}
Attempt: {retryAttempt} of 4

{retryMessage}

Update payment: https://buoy-ai.com/app/settings/billing

Best regards,
The Buoy Money Billing Team";

        return (html, text);
    }

    public static (string Html, string Text) PaymentSuccessful(
        string firstName,
        string planName,
        decimal amount,
        DateTime billingDate,
        DateTime nextBillingDate,
        string? invoiceUrl)
    {
        var invoiceSection = !string.IsNullOrEmpty(invoiceUrl)
            ? $@"<p><a href='{invoiceUrl}' style='color: #6366F1;'>View Invoice</a></p>"
            : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading("Payment Received ✓")}
        
        <p>Hi {firstName},</p>
        
        <p>Thank you for your payment! Your subscription has been renewed.</p>
        
        {EmailTemplateBase.SuccessBox($@"
            <strong>Payment successful:</strong> ${amount:F2}
        ")}
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Plan:</strong> {planName}<br>
            <strong>Billing date:</strong> {billingDate:MMMM d, yyyy}<br>
            <strong>Next billing date:</strong> {nextBillingDate:MMMM d, yyyy}
        ")}
        
        {invoiceSection}";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Billing", "✅", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Payment Received

Hi {firstName},

Thank you for your payment! Your subscription has been renewed.

Plan: {planName}
Amount: ${amount:F2}
Billing date: {billingDate:MMMM d, yyyy}
Next billing date: {nextBillingDate:MMMM d, yyyy}
{(!string.IsNullOrEmpty(invoiceUrl) ? $"\nView invoice: {invoiceUrl}" : "")}

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) TrialStarted(string firstName, int trialDays)
    {
        var trialEndDate = DateTime.UtcNow.AddDays(trialDays).ToString("MMMM dd, yyyy");
        
        var content = $@"
        {EmailTemplateBase.SectionHeading("Welcome to Buoy Money! 🎉")}
        
        <p>Hi {firstName},</p>
        
        <p>Thank you for starting your <strong>{trialDays}-day free trial</strong> of Buoy Money! You now have full access to all premium features.</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Trial ends:</strong> {trialEndDate}<br>
            <strong>Your card will be charged automatically</strong> unless you cancel before then.
        ")}
        
        <p><strong>Get started with these steps:</strong></p>
        <ol>
            <li><strong>Connect your bank accounts</strong> - Link your accounts through Plaid to see all your transactions</li>
            <li><strong>Set up budgets</strong> - Create budgets for different spending categories</li>
            <li><strong>Chat with Buoy Money</strong> - Ask questions about your finances and get personalized insights</li>
        </ol>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Go to Dashboard", "https://buoy-ai.com/app/dashboard")}
        </div>
        
        <p><strong>What's included in your trial:</strong></p>
        <ul>
            <li>✅ Unlimited bank account connections</li>
            <li>✅ AI-powered spending insights</li>
            <li>✅ Smart budget recommendations</li>
            <li>✅ Family sharing features</li>
            <li>✅ Priority support</li>
        </ul>
        
        <p>Have questions? Reply to this email and we'll be happy to help!</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "🎉", content, "Welcome aboard!<br>The Buoy Money Team");

        var text = $@"Welcome to Buoy Money!

Hi {firstName},

Thank you for starting your {trialDays}-day free trial of Buoy Money! You now have full access to all premium features.

Trial ends: {trialEndDate}
Your card will be charged automatically unless you cancel before then.

Get started:
1. Connect your bank accounts
2. Set up budgets
3. Chat with Buoy Money for personalized insights

Go to Dashboard: https://buoy-ai.com/app/dashboard

Welcome aboard!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) TrialEnding(string firstName, int daysRemaining, string planName, decimal price)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Your Trial Is Ending Soon")}
        
        <p>Hi {firstName},</p>
        
        {EmailTemplateBase.WarningBox($@"
            <strong>Your free trial ends in {daysRemaining} days.</strong>
        ")}
        
        <p>We hope you've been enjoying Buoy Money! To continue using all premium features, upgrade to a paid plan before your trial expires.</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>Recommended plan:</strong> {planName}<br>
            <strong>Price:</strong> ${price:F2}/month
        ")}
        
        <p><strong>What you'll keep with a paid plan:</strong></p>
        <ul>
            <li>Unlimited bank account connections</li>
            <li>AI-powered insights and categorization</li>
            <li>Family sharing features</li>
            <li>All your existing data and settings</li>
        </ul>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Upgrade Now", "https://buoy-ai.com/app/settings/billing")}
        </div>
        
        <p>Have questions? Reply to this email and we'll be happy to help!</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "⏰", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Your Trial Is Ending Soon

Hi {firstName},

Your free trial ends in {daysRemaining} days.

To continue using Buoy Money, upgrade before your trial expires.

Recommended: {planName} at ${price:F2}/month

Upgrade: https://buoy-ai.com/app/settings/billing

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) TrialExpired(string firstName)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Your Trial Has Ended")}
        
        <p>Hi {firstName},</p>
        
        <p>Your Buoy Money free trial has ended. We hope you enjoyed exploring all the features!</p>
        
        <p>Your account has been switched to our free plan, which includes:</p>
        <ul>
            <li>1 bank account connection</li>
            <li>Basic budgeting features</li>
            <li>Limited transaction history</li>
        </ul>
        
        <p>To unlock all premium features again, upgrade to a paid plan:</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("View Plans", "https://buoy-ai.com/pricing")}
        </div>
        
        <p>Don't worry - all your data is safe and will be available when you upgrade.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "📅", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Your Trial Has Ended

Hi {firstName},

Your Buoy Money free trial has ended.

Your account has been switched to our free plan. To unlock premium features, upgrade at:
https://buoy-ai.com/pricing

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    /// <summary>
    /// The day-0 email for a signup that was granted a trial. The trial has already started
    /// (cardless), so the only thing left to ask for is the bank connection.
    /// </summary>
    public static (string Html, string Text) TrialInvitation(
        string firstName, int trialDays, string accountsUrl = "https://buoy-ai.com/app/accounts")
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading($"Welcome to Buoy Money, {firstName}!")}

        <p>Hi {firstName},</p>

        <p>Your <strong>{trialDays}-day free trial</strong> has started. Everything is unlocked,
        and there's no card to add.</p>

        <p><strong>Get started in 3 steps:</strong></p>
        <ol>
            <li><strong>Connect your bank.</strong> About two minutes. You sign in through Plaid,
            so Buoy Money never sees your bank password, and the connection is read-only.</li>
            <li>Buoy Money sorts your transactions into categories.</li>
            <li>Build your first budget in one tap, from what you actually spend.</li>
        </ol>

        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Connect my bank", accountsUrl)}
        </div>

        <p>We'll remind you before your trial ends. After that, choose a plan to keep your data
        and features.</p>

        <p>Have questions? Just reply to this email.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "🚀", content, "Welcome aboard!<br>The Buoy Money Team");

        var text = $@"Welcome to Buoy Money, {firstName}!

Your {trialDays}-day free trial has started. Everything is unlocked, and there's no card to add.

Get started in 3 steps:
1. Connect your bank. About two minutes. You sign in through Plaid, so Buoy Money never sees your bank password, and the connection is read-only.
2. Buoy Money sorts your transactions into categories.
3. Build your first budget in one tap, from what you actually spend.

Connect my bank: {accountsUrl}

We'll remind you before your trial ends. After that, choose a plan to keep your data and features.

Have questions? Just reply to this email.

Welcome aboard!
The Buoy Money Team";

        return (html, text);
    }
}
