namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Email templates for user onboarding and account management emails.
/// </summary>
public static class AccountEmailTemplates
{
    /// <summary>
    /// Plain welcome. Since the day-0 consolidation it is only sent when no trial was granted;
    /// a trial signup gets <see cref="BillingEmailTemplates.TrialInvitation"/> instead. Either
    /// way the one button is "Connect my bank", because that is the step everything else in
    /// the app depends on.
    /// </summary>
    public static (string Html, string Text) Welcome(
        string firstName, bool hasTrial, int? trialDays, string accountsUrl = "https://buoy-ai.com/app/accounts")
    {
        // Trials are cardless and start at signup. This box used to say "add a payment method
        // in your Profile" to start one, which was never true.
        var trialSection = hasTrial && trialDays.HasValue
            ? $@"
            {EmailTemplateBase.InfoBox($@"
                <strong>Your {trialDays}-day free trial is on.</strong><br>
                No card needed. Everything is unlocked while it runs.
            ")}"
            : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading("Welcome to Buoy Money!")}

        <p>Hi {firstName},</p>

        <p>Thanks for signing up. The first step is connecting your bank: it takes about two
        minutes, and it's what everything else in Buoy Money runs on.</p>

        {trialSection}

        <p><strong>Once your bank is connected:</strong></p>
        <ul>
            <li>📊 <strong>Your spending sorts itself</strong> into categories</li>
            <li>💰 <strong>Build a budget</strong> from what you actually spend</li>
            <li>🎯 <strong>Savings goals</strong> show your progress</li>
            <li>👨‍👩‍👧‍👦 <strong>Family sharing</strong> if you budget with someone</li>
        </ul>

        <p style='font-size:14px;color:#6b7280'>You sign in to your bank through Plaid, so Buoy Money
        never sees your bank password, and the connection is read-only.</p>

        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Connect my bank", accountsUrl)}
        </div>

        <p>Have questions? Just reply to this email.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "🌊", content, "Welcome aboard!<br>The Buoy Money Team");

        var text = $@"Welcome to Buoy Money!

Hi {firstName},

Thanks for signing up. The first step is connecting your bank: it takes about two minutes, and it's what everything else in Buoy Money runs on.
{(hasTrial && trialDays.HasValue ? $"\nYour {trialDays}-day free trial is on. No card needed.\n" : "")}
Once your bank is connected:
- Your spending sorts itself into categories
- Build a budget from what you actually spend
- Savings goals show your progress
- Family sharing if you budget with someone

You sign in to your bank through Plaid, so Buoy Money never sees your bank password, and the connection is read-only.

Connect my bank: {accountsUrl}

Have questions? Just reply to this email.

Welcome aboard!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) AccountVerification(string verificationLink)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Verify Your Email Address")}
        
        <p>Thanks for signing up for Buoy Money! Please verify your email address by clicking the button below:</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Verify Email", verificationLink)}
        </div>
        
        <p>Or copy and paste this link into your browser:</p>
        <p style='word-break: break-all; color: #6366F1;'>{verificationLink}</p>
        
        <p><strong>This link will expire in 24 hours.</strong></p>
        
        <p>If you didn't create a Buoy Money account, you can safely ignore this email.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "✉️", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Verify Your Email Address

Thanks for signing up for Buoy Money! Please verify your email address:

{verificationLink}

This link will expire in 24 hours.

If you didn't create a Buoy Money account, you can safely ignore this email.

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) EmailChanged(string firstName, string newEmail)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Email Address Updated")}
        
        <p>Hi {firstName},</p>
        
        <p>Your Buoy Money account email address has been changed to:</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>{newEmail}</strong>
        ")}
        
        <p>If you made this change, no action is needed.</p>
        
        <p>If you didn't change your email address, please contact our support team immediately.</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("Contact Support", "mailto:support@buoy-ai.com")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "📧", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Email Address Updated

Hi {firstName},

Your Buoy Money account email address has been changed to: {newEmail}

If you made this change, no action is needed.

If you didn't change your email address, please contact support@buoy-ai.com immediately.

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) AccountDeletionWarning(string firstName, int daysRemaining)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Your Account Will Be Deleted")}
        
        <p>Hi {firstName},</p>
        
        {EmailTemplateBase.WarningBox($@"
            <strong>Your Buoy Money account is scheduled for deletion in {daysRemaining} days.</strong>
        ")}
        
        <p>As requested, your account and all associated data will be permanently deleted. This includes:</p>
        <ul>
            <li>All linked bank accounts and transaction history</li>
            <li>Budgets and budget history</li>
            <li>Savings goals and progress</li>
            <li>Family memberships and shared data</li>
        </ul>
        
        <p>If you've changed your mind and want to keep your account, click the button below to cancel the deletion:</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Keep My Account", "https://buoy-ai.com/app/settings/account")}
        </div>
        
        <p>If you still want to proceed with deletion, no action is needed.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "⚠️", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"Your Account Will Be Deleted

Hi {firstName},

Your Buoy Money account is scheduled for deletion in {daysRemaining} days.

If you've changed your mind, visit: https://buoy-ai.com/app/settings/account

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) AccountDeleted(string firstName)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("Account Deleted")}
        
        <p>Hi {firstName},</p>
        
        <p>Your Buoy Money account has been successfully deleted as requested.</p>
        
        <p>All your data has been permanently removed from our systems, including:</p>
        <ul>
            <li>Account credentials and profile information</li>
            <li>Linked bank accounts and transaction history</li>
            <li>Budgets, savings goals, and financial data</li>
        </ul>
        
        <p>We're sorry to see you go. If you ever want to come back, you're always welcome to create a new account.</p>
        
        <p>Thank you for being part of Buoy Money.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "👋", content, "Take care,<br>The Buoy Money Team");

        var text = $@"Account Deleted

Hi {firstName},

Your Buoy Money account has been successfully deleted as requested.

All your data has been permanently removed from our systems.

Thank you for being part of Buoy Money.

Take care,
The Buoy Money Team";

        return (html, text);
    }
}
