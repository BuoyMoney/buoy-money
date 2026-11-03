namespace BuoyAI.Infrastructure.Email;

/// <summary>
/// Email templates for gamification and engagement features.
/// </summary>
public static class GamificationEmailTemplates
{
    public static (string Html, string Text) BadgeEarned(
        string firstName,
        string badgeName,
        string badgeDescription,
        string badgeIconUrl,
        int totalBadges)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("🏆 New Badge Earned!")}
        
        <p>Hi {firstName},</p>
        
        {EmailTemplateBase.SuccessBox($@"
            <strong>Congratulations!</strong> You've earned a new badge!
        ")}
        
        <div style='text-align: center; margin: 30px 0;'>
            <div style='display: inline-block; background: linear-gradient(135deg, #6366F1 0%, #4338CA 100%); border-radius: 50%; padding: 30px; margin-bottom: 16px;'>
                <span style='font-size: 48px;'>🏅</span>
            </div>
            <h3 style='color: #1F2937; margin: 0 0 8px 0;'>{badgeName}</h3>
            <p style='color: #6B7280; margin: 0;'>{badgeDescription}</p>
        </div>
        
        <p style='text-align: center; color: #6B7280;'>
            You now have <strong>{totalBadges} badges</strong> in your collection!
        </p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("View All Badges", "https://buoy-ai.com/app/achievements")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Achievements", "🏆", content, "Keep up the great work!<br>The Buoy Money Team");

        var text = $@"🏆 New Badge Earned!

Hi {firstName},

Congratulations! You've earned a new badge!

Badge: {badgeName}
{badgeDescription}

You now have {totalBadges} badges in your collection!

View all badges: https://buoy-ai.com/app/achievements

Keep up the great work!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) MilestoneReached(
        string firstName,
        string milestoneName,
        string milestoneDescription,
        int milestoneValue,
        string milestoneUnit)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("🎯 Milestone Reached!")}
        
        <p>Hi {firstName},</p>
        
        {EmailTemplateBase.SuccessBox($@"
            <strong>Amazing achievement!</strong> You've reached a new milestone!
        ")}
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.StatBox(milestoneName, $"{milestoneValue:N0} {milestoneUnit}", "#10B981")}
        </div>
        
        <p style='text-align: center; color: #6B7280;'>{milestoneDescription}</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.SecondaryButton("View Achievements", "https://buoy-ai.com/app/achievements")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Achievements", "🎯", content, "Onwards and upwards!<br>The Buoy Money Team");

        var text = $@"🎯 Milestone Reached!

Hi {firstName},

Amazing achievement! You've reached a new milestone!

{milestoneName}: {milestoneValue:N0} {milestoneUnit}
{milestoneDescription}

View achievements: https://buoy-ai.com/app/achievements

Onwards and upwards!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) StreakNotification(
        string firstName,
        int streakDays,
        string streakType,
        bool isAtRisk)
    {
        var streakTitle = isAtRisk
            ? $"⚠️ Your {streakDays}-Day Streak is at Risk!"
            : $"🔥 {streakDays}-Day Streak!";

        var streakMessage = isAtRisk
            ? $"Log in today to keep your {streakType} streak alive!"
            : $"Congratulations on maintaining your {streakType} streak!";

        var alertBox = isAtRisk
            ? EmailTemplateBase.WarningBox($"<strong>Don't break your streak!</strong> You haven't logged in today.")
            : EmailTemplateBase.SuccessBox($"<strong>You're on fire!</strong> Keep up the great work!");

        var content = $@"
        {EmailTemplateBase.SectionHeading(streakTitle)}
        
        <p>Hi {firstName},</p>
        
        {alertBox}
        
        <div style='text-align: center; margin: 30px 0;'>
            <div style='font-size: 64px;'>🔥</div>
            <h2 style='color: #F59E0B; margin: 8px 0;'>{streakDays} Days</h2>
            <p style='color: #6B7280; margin: 0;'>{streakType}</p>
        </div>
        
        <p style='text-align: center;'>{streakMessage}</p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Continue Streak", "https://buoy-ai.com/app/dashboard")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Streaks", "🔥", content, "Keep the momentum going!<br>The Buoy Money Team");

        var text = $@"{streakTitle}

Hi {firstName},

{(isAtRisk ? "Don't break your streak! You haven't logged in today." : "You're on fire! Keep up the great work!")}

Streak: {streakDays} days ({streakType})

{streakMessage}

Continue streak: https://buoy-ai.com/app/dashboard

Keep the momentum going!
The Buoy Money Team";

        return (html, text);
    }

    public static (string Html, string Text) WeeklyChallenge(
        string firstName,
        string challengeName,
        string challengeDescription,
        decimal challengeTarget,
        string challengeUnit,
        int daysRemaining)
    {
        var content = $@"
        {EmailTemplateBase.SectionHeading("🎮 Weekly Challenge")}
        
        <p>Hi {firstName},</p>
        
        <p>A new weekly challenge is waiting for you!</p>
        
        {EmailTemplateBase.InfoBox($@"
            <strong>{challengeName}</strong><br>
            {challengeDescription}
        ")}
        
        <div style='text-align: center; margin: 20px 0;'>
            {EmailTemplateBase.StatBox("Target", $"{challengeTarget:N0} {challengeUnit}", "#6366F1")}
        </div>
        
        <p style='text-align: center; color: #6B7280;'>
            <strong>{daysRemaining} days remaining</strong> to complete this challenge
        </p>
        
        <div style='text-align: center; margin: 30px 0;'>
            {EmailTemplateBase.PrimaryButton("Start Challenge", "https://buoy-ai.com/app/challenges")}
        </div>";

        var html = EmailTemplateBase.WrapInTemplate("Buoy Money Challenges", "🎮", content, "Good luck!<br>The Buoy Money Team");

        var text = $@"🎮 Weekly Challenge

Hi {firstName},

A new weekly challenge is waiting for you!

Challenge: {challengeName}
{challengeDescription}

Target: {challengeTarget:N0} {challengeUnit}
Time remaining: {daysRemaining} days

Start challenge: https://buoy-ai.com/app/challenges

Good luck!
The Buoy Money Team";

        return (html, text);
    }
}
