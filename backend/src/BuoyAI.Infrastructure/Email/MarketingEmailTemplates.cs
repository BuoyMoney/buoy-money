namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Email templates for marketing, announcements, and product updates.
/// </summary>
public static class MarketingEmailTemplates
{
    public static (string Html, string Text) FeatureAnnouncement(
        string firstName,
        string featureName,
        string featureDescription,
        string ctaText,
        string ctaUrl,
        string? imageUrl)
    {
        var imageSection = !string.IsNullOrEmpty(imageUrl)
            ? $@"<div style='text-align: center; margin: 20px 0;'>
                    <img src='{imageUrl}' alt='{featureName}' style='max-width: 100%; height: auto; border-radius: 8px;' />
                </div>"
            : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading($"✨ New Feature: {featureName}")}
        
        <p>Hi {firstName},</p>
        
        <p>We're excited to announce a brand new feature in Buoy Money!</p>
        
        {imageSection}
        
        {EmailTemplateBase.InfoBox($@"
            <strong>{featureName}</strong><br><br>
            {featureDescription}
        ")}
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton(ctaText, ctaUrl)}
        </div>
        
        <p>We'd love to hear your feedback! Reply to this email to let us know what you think.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money News", "✨", content, "Happy budgeting!<br>The Buoy Money Team");

        var text = $@"✨ New Feature: {featureName}

Hi {firstName},

We're excited to announce a brand new feature in Buoy Money!

{featureName}
{featureDescription}

Try it out: {ctaUrl}

We'd love to hear your feedback!

Happy budgeting!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) ProductUpdate(
        string firstName,
        string updateTitle,
        List<string> improvements,
        List<string> bugFixes,
        string? releaseNotesUrl)
    {
        var improvementsHtml = improvements.Count > 0
            ? $@"<p><strong>🚀 Improvements:</strong></p>
                <ul>
                    {string.Join("\n", improvements.Select(i => $"<li>{i}</li>"))}
                </ul>"
            : "";

        var bugFixesHtml = bugFixes.Count > 0
            ? $@"<p><strong>🐛 Bug Fixes:</strong></p>
                <ul>
                    {string.Join("\n", bugFixes.Select(b => $"<li>{b}</li>"))}
                </ul>"
            : "";

        var releaseNotesSection = !string.IsNullOrEmpty(releaseNotesUrl)
            ? $@"<div style='text-align: center; margin: 30px 0;'>
                    {EmailTemplateBase.SecondaryButton("View Full Release Notes", releaseNotesUrl)}
                </div>"
            : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading(updateTitle)}
        
        <p>Hi {firstName},</p>
        
        <p>We've been hard at work improving Buoy Money! Here's what's new:</p>
        
        {improvementsHtml}
        
        {bugFixesHtml}
        
        {releaseNotesSection}
        
        <p>Thank you for being part of our community!</p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Update", "🔄", content, "Best regards,<br>The Buoy Money Team");

        var improvementsText = improvements.Count > 0
            ? "Improvements:\n" + string.Join("\n", improvements.Select(i => $"• {i}"))
            : "";

        var bugFixesText = bugFixes.Count > 0
            ? "Bug Fixes:\n" + string.Join("\n", bugFixes.Select(b => $"• {b}"))
            : "";

        var text = $@"{updateTitle}

Hi {firstName},

We've been hard at work improving Buoy Money! Here's what's new:

{improvementsText}

{bugFixesText}
{(!string.IsNullOrEmpty(releaseNotesUrl) ? $"\nRelease notes: {releaseNotesUrl}" : "")}

Thank you for being part of our community!

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) TipsAndBestPractices(
        string firstName,
        string tipTitle,
        string tipContent,
        string? tipImageUrl,
        List<(string title, string description)>? additionalTips)
    {
        var imageSection = !string.IsNullOrEmpty(tipImageUrl)
            ? $@"<div style='text-align: center; margin: 20px 0;'>
                    <img src='{tipImageUrl}' alt='{tipTitle}' style='max-width: 100%; height: auto; border-radius: 8px;' />
                </div>"
            : "";

        var additionalTipsHtml = additionalTips?.Count > 0
            ? $@"
                {EmailTemplateBase.Divider()}
                <p><strong>More Tips:</strong></p>
                {string.Join("\n", additionalTips.Select(t => $@"
                    <div style='margin: 16px 0; padding: 16px; background: #F9FAFB; border-radius: 8px;'>
                        <p style='margin: 0 0 8px 0; font-weight: bold; color: #1F2937;'>💡 {t.title}</p>
                        <p style='margin: 0; color: #6B7280;'>{t.description}</p>
                    </div>
                "))}"
            : "";

        var content = $@"
        {EmailTemplateBase.SectionHeading($"💡 {tipTitle}")}
        
        <p>Hi {firstName},</p>
        
        {imageSection}
        
        <div style='line-height: 1.6;'>
            {tipContent}
        </div>
        
        {additionalTipsHtml}
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("Explore Buoy Money", "https://buoy-ai.com/app/dashboard")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Tips", "💡", content, "To your financial success!<br>The Buoy Money Team");

        var additionalTipsText = additionalTips?.Count > 0
            ? "\nMore Tips:\n" + string.Join("\n\n", additionalTips.Select(t => $"💡 {t.title}\n{t.description}"))
            : "";

        var text = $@"💡 {tipTitle}

Hi {firstName},

{tipContent}
{additionalTipsText}

To your financial success!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) PromotionalOffer(
        string firstName,
        string offerTitle,
        string offerDescription,
        string discountCode,
        int discountPercent,
        DateTime expirationDate,
        string ctaUrl)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading($"🎁 {offerTitle}")}
        
        <p>Hi {firstName},</p>
        
        <p>We have a special offer just for you!</p>
        
        <div style='text-align: center; margin: 30px 0; padding: 30px; background: linear-gradient(135deg, #6366F1 0%, #4338CA 100%); border-radius: 12px;'>
            <p style='color: white; font-size: 24px; margin: 0 0 8px 0; font-weight: bold;'>{discountPercent}% OFF</p>
            <p style='color: rgba(255,255,255,0.9); margin: 0;'>{offerDescription}</p>
        </div>
        
        {EmailTemplateBase.CodeBox(discountCode)}
        
        <p style='text-align: center; color: #EF4444; font-weight: bold;'>
            ⏰ Expires {expirationDate:MMMM d, yyyy}
        </p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Claim Your Discount", ctaUrl)}
        </div>
        
        <p style='color: #6B7280; font-size: 14px;'>
            Terms and conditions apply. Cannot be combined with other offers.
        </p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Special Offer", "🎁", content, "Best regards,<br>The Buoy Money Team");

        var text = $@"🎁 {offerTitle}

Hi {firstName},

We have a special offer just for you!

{discountPercent}% OFF - {offerDescription}

Use code: {discountCode}

Expires: {expirationDate:MMMM d, yyyy}

Claim your discount: {ctaUrl}

Terms and conditions apply.

Best regards,
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) ReEngagement(
        string firstName,
        int daysSinceLastVisit,
        string? personalizedMessage)
    {
        var timeAwayMessage = daysSinceLastVisit switch
        {
            <= 7 => "It's been a few days",
            <= 14 => "It's been a week or so",
            <= 30 => "It's been a few weeks",
            <= 60 => "It's been over a month",
            _ => "It's been a while"
        };

        var message = personalizedMessage ?? "Your budgets are waiting, and we'd love to help you stay on track with your financial goals.";

        var content = $@"
        {EmailTemplateBase.SectionHeading("We Miss You! 👋")}
        
        <p>Hi {firstName},</p>
        
        <p>{timeAwayMessage} since we last saw you on Buoy Money!</p>
        
        <p>{message}</p>
        
        <p><strong>Here's what you might have missed:</strong></p>
        <ul>
            <li>📊 New transactions waiting to be categorized</li>
            <li>💰 Updates to your budgets and spending trends</li>
            <li>🎯 Progress on your savings goals</li>
            <li>✨ New features and improvements</li>
        </ul>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Return to Buoy Money", "https://buoy-ai.com/app/dashboard")}
        </div>
        
        <p style='color: #6B7280; font-size: 14px;'>
            Not interested anymore? You can <a href='https://buoy-ai.com/app/settings/notifications' style='color: #6366F1;'>update your email preferences</a> at any time.
        </p>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money", "👋", content, "We're here when you're ready!<br>The Buoy Money Team");

        var text = $@"We Miss You! 👋

Hi {firstName},

{timeAwayMessage} since we last saw you on Buoy Money!

{message}

Here's what you might have missed:
• New transactions waiting to be categorized
• Updates to your budgets and spending trends
• Progress on your savings goals
• New features and improvements

Return to Buoy Money: https://buoy-ai.com/app/dashboard

We're here when you're ready!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) ReferralInvite(
        string firstName,
        string referralCode,
        string referralLink,
        int rewardAmount,
        string rewardType)
    {
        var rewardText = rewardType.ToLowerInvariant() switch
        {
            "cash" => $"${rewardAmount}",
            "credit" => $"${rewardAmount} account credit",
            "months" => $"{rewardAmount} free month{(rewardAmount > 1 ? "s" : "")}",
            _ => $"{rewardAmount} {rewardType}"
        };

        var content = $@"
        {EmailTemplateBase.SectionHeading("🎉 Share Buoy Money, Get Rewards!")}
        
        <p>Hi {firstName},</p>
        
        <p>Love Buoy Money? Share it with friends and family and you'll both get rewarded!</p>
        
        <div style='text-align: center; margin: 30px 0; padding: 30px; background: linear-gradient(135deg, #10B981 0%, #059669 100%); border-radius: 12px;'>
            <p style='color: white; font-size: 24px; margin: 0 0 8px 0; font-weight: bold;'>Get {rewardText}</p>
            <p style='color: rgba(255,255,255,0.9); margin: 0;'>For every friend who signs up!</p>
        </div>
        
        <p><strong>Your referral code:</strong></p>
        {EmailTemplateBase.CodeBox(referralCode)}
        
        <p><strong>Or share this link:</strong></p>
        <p style='word-break: break-all; color: #6366F1; text-align: center;'>{referralLink}</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Share Now", referralLink)}
        </div>
        
        <p><strong>How it works:</strong></p>
        <ol>
            <li>Share your link or code with friends</li>
            <li>They sign up using your referral</li>
            <li>You both get {rewardText}!</li>
        </ol>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Referrals", "🎉", content, "Happy referring!<br>The Buoy Money Team");

        var text = $@"🎉 Share Buoy Money, Get Rewards!

Hi {firstName},

Love Buoy Money? Share it with friends and family and you'll both get rewarded!

Get {rewardText} for every friend who signs up!

Your referral code: {referralCode}
Or share this link: {referralLink}

How it works:
1. Share your link or code with friends
2. They sign up using your referral
3. You both get {rewardText}!

Happy referring!
The Buoy Money Team";

        return (html, text);
    }

    /// <summary>
    /// Confirmation for an email captured on the marketing site in exchange for a longer
    /// free trial. This is the first message a lead ever gets from us, so it does two jobs:
    /// it proves the offer is real (state the days plainly, link straight to sign-up), and
    /// where the lead came from the calculator it returns the numbers they asked us to save.
    /// </summary>

    /// <summary>
    /// One reminder that a reserved trial is still waiting, sent to a lead who gave us
    /// their email and never registered.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter and quieter than the original offer. They have already seen the
    /// pitch; repeating it louder is how a helpful nudge becomes the reason someone
    /// unsubscribes. Sent once, ever.
    /// </remarks>
    public static (string Html, string Text) ExtendedTrialReminder(
        string registerUrl,
        string unsubscribeUrl)
    {
        // No day count on purpose: leads captured under the old offer were promised a different
        // length, and the longer trial applies itself at sign-up either way.
        var content = $@"
        {EmailTemplateBase.SectionHeading("Your longer free trial is still reserved")}

        <p>A little while back you asked me to hold a longer free trial of Buoy Money for you. It is
        still here whenever you want it.</p>

        {EmailTemplateBase.InfoBox(@"
            <strong>Your extended free trial</strong><br><br>
            No card required to start.
        ")}

        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Start my free trial", registerUrl)}
        </div>

        <p>Sign up with <strong>this same email address</strong> and the longer trial applies
        itself. Nothing to enter.</p>

        <p style='font-size:13px;color:#6b7280'>This is the only reminder I will send about
        it.</p>
        {UnsubscribeFooter(unsubscribeUrl)}";

        var html = EmailTemplateBase.WrapInTemplate(
            "Your Buoy Money trial is waiting", "", content, "Whenever you are ready,<br>Jonathan at Buoy Money");

        var text = @"Your longer free trial is still reserved

A little while back you asked me to hold a longer free trial of Buoy Money for you.
It is still here whenever you want it. No card required to start.

Start your trial: " + registerUrl + @"

Sign up with this same email address and the longer trial applies itself.

This is the only reminder I will send about it.

Unsubscribe: " + unsubscribeUrl + @"

Whenever you are ready,
Jonathan at Buoy Money";

        return (html, text);
    }

    /// <summary>Plain-English length for the two trial lengths we advertise.</summary>
    public static string DaysPhrase(int days) => days switch
    {
        14 => "two weeks",
        30 => "a month",
        _ => $"{days} days"
    };

    public static (string Html, string Text) ExtendedTrialOffer(
        int trialDays,
        int standardTrialDays,
        string registerUrl,
        CalculatorLines? calculator,
        string unsubscribeUrl)
    {
        var length = DaysPhrase(trialDays);
        var standardLength = DaysPhrase(standardTrialDays);
        var bonusLine = trialDays > standardTrialDays
            ? $"That's {length} free instead of the usual {standardLength}."
            : $"That's {length} to try everything.";

        var calcHtml = calculator == null
            ? ""
            : $@"
        {EmailTemplateBase.SectionHeading("Your saved numbers")}
        {EmailTemplateBase.InfoBox($@"
            <strong>Monthly budget:</strong> {calculator.MonthlyBudget}<br>
            <strong>Daily allowance:</strong> {calculator.DailyAllowance}<br>
            <strong>Banked by skipping {calculator.SkipsPerWeek} day(s) a week:</strong> {calculator.Banked}<br>
            <strong>Over a year:</strong> {calculator.Yearly}
        ")}
        <p>Those are the numbers from the calculator. Inside Buoy Money they update themselves
        against your real spending, every day.</p>";

        var content = $@"
        {EmailTemplateBase.SectionHeading($"Your free trial is reserved: {length}")}

        <p>Thanks for your interest in Buoy Money.</p>

        {EmailTemplateBase.InfoBox($@"
            <strong>{length} free</strong><br><br>
            {bonusLine} No card required to start.
        ")}
        {calcHtml}
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton($"Start my free trial", registerUrl)}
        </div>

        <p>Sign up with <strong>this same email address</strong> and the longer trial is
        applied automatically. Nothing to enter, no code to remember.</p>

        <p style='font-size:13px;color:#6b7280'>I'll send you a short series of tips on
        getting the most out of Buoy, and occasional product news. That's it, and you can stop
        them at any time.</p>
        {UnsubscribeFooter(unsubscribeUrl)}";

        var html = EmailTemplateBase.WrapInTemplate(
            "Your Buoy Money trial", "", content, "See you on board,<br>Jonathan at Buoy Money");

        var calcText = calculator == null
            ? ""
            : $@"
YOUR SAVED NUMBERS
Monthly budget: {calculator.MonthlyBudget}
Daily allowance: {calculator.DailyAllowance}
Banked by skipping {calculator.SkipsPerWeek} day(s) a week: {calculator.Banked}
Over a year: {calculator.Yearly}
";

        var text = $@"Your free trial is reserved: {length}

Thanks for your interest in Buoy Money.

{length.ToUpperInvariant()} FREE
{bonusLine} No card required to start.
{calcText}
Start here: {registerUrl}

Sign up with this same email address and the longer trial is applied
automatically. Nothing to enter, no code to remember.

I'll send you a short series of tips on getting the most out of Buoy, and
occasional product news. That's it, and you can stop them at any time.

Unsubscribe: {unsubscribeUrl}

See you on board,
Jonathan at Buoy Money";

        return (html, text);
    }

    /// <summary>Pre-formatted calculator figures for <see cref="ExtendedTrialOffer"/>.</summary>
    public record CalculatorLines(
        string MonthlyBudget,
        string DailyAllowance,
        string Banked,
        string Yearly,
        int SkipsPerWeek);

    /// <summary>
    /// Reply for a capture from an address that already has an account, or that already used
    /// its lifetime free trial.
    ///
    /// The offer email would tell these people to sign up for the longer trial, which
    /// registration would then refuse. Rather than break a promise, acknowledge what they
    /// asked for (their calculator numbers, if any) and send them to sign in.
    /// </summary>
    public static (string Html, string Text) ExistingAccountOffer(
        string loginUrl,
        CalculatorLines? calculator,
        string unsubscribeUrl)
    {
        var calcHtml = calculator == null
            ? ""
            : $@"
        {EmailTemplateBase.SectionHeading("Your saved numbers")}
        {EmailTemplateBase.InfoBox($@"
            <strong>Monthly budget:</strong> {calculator.MonthlyBudget}<br>
            <strong>Daily allowance:</strong> {calculator.DailyAllowance}<br>
            <strong>Banked by skipping {calculator.SkipsPerWeek} day(s) a week:</strong> {calculator.Banked}<br>
            <strong>Over a year:</strong> {calculator.Yearly}
        ")}";

        var content = $@"
        {EmailTemplateBase.SectionHeading("You already have a Buoy account")}

        <p>Thanks for getting in touch.</p>

        <p>This address is already registered, so there's no new trial to set up. Everything is
        waiting where you left it.</p>
        {calcHtml}
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Sign in to Buoy", loginUrl)}
        </div>

        <p>If you didn't expect this email, someone may have typed your address on my site.
        Nothing has changed on your account, and you can safely ignore this.</p>
        {UnsubscribeFooter(unsubscribeUrl)}";

        var html = EmailTemplateBase.WrapInTemplate(
            "Your Buoy Money account", "", content, "Good to see you again,<br>Jonathan at Buoy Money");

        var calcText = calculator == null
            ? ""
            : $@"
YOUR SAVED NUMBERS
Monthly budget: {calculator.MonthlyBudget}
Daily allowance: {calculator.DailyAllowance}
Banked by skipping {calculator.SkipsPerWeek} day(s) a week: {calculator.Banked}
Over a year: {calculator.Yearly}
";

        var text = $@"You already have a Buoy account

Thanks for getting in touch.

This address is already registered, so there's no new trial to set up.
Everything is waiting where you left it.
{calcText}
Sign in: {loginUrl}

If you didn't expect this email, someone may have typed your address on my
site. Nothing has changed on your account, and you can safely ignore this.

Unsubscribe: {unsubscribeUrl}

Good to see you again,
Jonathan at Buoy Money";

        return (html, text);
    }

    /// <summary>
    /// Opt-out URL carrying the lead's opaque token.
    ///
    /// The host has to come from the caller. This used to hardcode buoy-ai.com, which meant
    /// every email sent from staging carried a link to the production site, where that token
    /// does not exist and (until this ships) neither does the page. Clicking it did nothing.
    /// </summary>
    /// <summary>
    /// Invite prompt carrying the user's own referral code and link.
    ///
    /// States the offer on BOTH sides explicitly. "Invite friends and earn rewards" is the
    /// version nobody acts on, because the reader cannot tell what they or their friend
    /// actually get. It also names the condition — the friend has to connect an account —
    /// rather than letting someone believe a signup alone pays.
    /// </summary>
    public static (string html, string text) ReferralInvite(
        string firstName,
        string referralCode,
        string referralLink)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("🤝 Know someone who'd like Buoy Money?")}

        <p>Hi {firstName},</p>

        <p>If Buoy Money has been useful to you, share it. When a friend joins with your link and
        connects an account, <strong>you both get a free month</strong>.</p>

        {EmailTemplateBase.InfoBox($@"
            <strong>Your invite code</strong><br><br>
            <span style='font-size: 22px; letter-spacing: 3px; font-family: monospace;'>{referralCode}</span>
        ")}

        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Share your invite", referralLink)}
        </div>

        <p style='color: #6b7280; font-size: 13px;'>The free month lands once your friend has
        connected an account, so you will see it shortly after they finish setting up.</p>";

        var html = EmailTemplateBase.WrapInTemplate("Invite a friend", "🤝", content, "Thanks for spreading the word!<br>The Buoy Money Team");

        var text = $@"🤝 Know someone who'd like Buoy Money?

Hi {firstName},

If Buoy Money has been useful to you, share it. When a friend joins with your link and connects
an account, you both get a free month.

Your invite code: {referralCode}

Share your invite: {referralLink}

The free month lands once your friend has connected an account, so you will see it shortly
after they finish setting up.

Thanks for spreading the word!
The Buoy Money Team";

        return (html, text);
    }

    public static string UnsubscribeUrl(string baseUrl, string token) =>
        $"{baseUrl.TrimEnd('/')}/unsubscribe?t={Uri.EscapeDataString(token)}";

    /// <summary>
    /// Required on every marketing send to a lead. These addresses have no account, so there
    /// is no preferences screen for them to use; this link is their only way out.
    /// </summary>
    private static string UnsubscribeFooter(string unsubscribeUrl) => $@"
        <p style='font-size:12px;color:#9ca3af;text-align:center;margin-top:26px'>
            Don't want these? <a href='{unsubscribeUrl}' style='color:#9ca3af'>Unsubscribe</a>.
        </p>";
}
