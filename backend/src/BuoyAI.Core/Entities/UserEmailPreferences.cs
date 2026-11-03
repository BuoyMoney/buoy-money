using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// User's email notification preferences
/// Controls which types of emails the user wants to receive
/// Security-critical emails (password reset, MFA, security alerts) always send regardless of preferences
/// </summary>
public class UserEmailPreferences
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    
    // ===== ACCOUNT ACTIVITY NOTIFICATIONS (Configurable) =====
    
    /// <summary>
    /// Plaid connection issues: login required, expiration warnings, etc.
    /// </summary>
    public bool PlaidConnectionIssues { get; set; } = true;
    
    /// <summary>
    /// Notifications when Plaid connections are successfully restored
    /// </summary>
    public bool PlaidReconnectionSuccess { get; set; } = true;
    
    /// <summary>
    /// Subscription changes: billing plan changes, payment failures, etc.
    /// </summary>
    public bool SubscriptionChanges { get; set; } = true;
    
    /// <summary>
    /// Trial expiration warnings
    /// </summary>
    public bool TrialExpirationWarnings { get; set; } = true;
    
    // ===== FINANCIAL INSIGHTS (Configurable) =====
    
    /// <summary>
    /// Budget overspending alerts and warnings
    /// </summary>
    public bool BudgetAlerts { get; set; } = true;
    
    /// <summary>
    /// Savings goal milestone achievements
    /// </summary>
    public bool SavingsGoalMilestones { get; set; } = true;
    
    /// <summary>
    /// Alerts for unusually large transactions
    /// </summary>
    public bool LargeTransactionAlerts { get; set; } = false; // Opt-in by default
    
    /// <summary>
    /// Monthly financial summary reports.
    /// Default ON: this is a report about the user's OWN data, the closest of these to a
    /// service email, and the industry norm for a finance product.
    /// </summary>
    public bool MonthlySummary { get; set; } = true; // Opt-out (default on)
    
    // ===== MARKETING & UPDATES (Configurable) =====
    
    /// <summary>
    /// Announcements about new BuoyAI features.
    /// Default ON: product-level news about a product the user actively holds an account
    /// for, same reasoning as ProductAnnouncements below.
    /// </summary>
    public bool FeatureAnnouncements { get; set; } = true; // Opt-out (default on)
    
    /// <summary>
    /// Financial tips and best practices.
    /// Default ON: educational content is the main engagement lever for users who have
    /// signed up but not yet activated.
    /// </summary>
    public bool TipsAndBestPractices { get; set; } = true; // Opt-out (default on)
    
    /// <summary>
    /// Product updates and changes.
    /// Default ON: service-adjacent news about a product in active use.
    /// </summary>
    public bool ProductUpdates { get; set; } = true; // Opt-out (default on)
    
    /// <summary>
    /// Promotional offers and special deals.
    ///
    /// DELIBERATELY still opt-in, while the four categories above moved to opt-out. This is
    /// the only one that is unambiguously advertising rather than news about a product the
    /// user already holds, and it is the category where defaulting to ON carries real
    /// exposure: GDPR/PECR require prior consent for direct marketing to EU/UK recipients.
    /// The same reasoning already made analytics opt-in (#606).
    /// </summary>
    public bool PromotionalOffers { get; set; } = false; // Opt-in by default

    /// <summary>
    /// Prompts to invite a friend using your referral code.
    ///
    /// Default ON, but its own category on purpose: an invite nag is the most annoying thing
    /// in this list if you have no interest in referring anyone, and burying it inside
    /// ProductUpdates would force people to mute genuine product news to escape it.
    /// </summary>
    public bool ReferralInvites { get; set; } = true; // Opt-out (default on)

    /// <summary>
    /// Product announcements broadcast by an admin (e.g. major feature launches).
    /// Default ON (opt-out): these are infrequent, product-level service updates — distinct
    /// from incident comms (which override marketing opt-outs entirely) and from the opt-in
    /// FeatureAnnouncements/ProductUpdates marketing categories. See admin broadcast (#552).
    /// </summary>
    public bool ProductAnnouncements { get; set; } = true; // Opt-out (default on)
    
    // ===== ACHIEVEMENTS & GAMIFICATION (Configurable) =====
    
    /// <summary>
    /// Notifications when earning achievement badges
    /// </summary>
    public bool BadgeEarned { get; set; } = true;
    
    /// <summary>
    /// Financial milestone achievements
    /// </summary>
    public bool MilestoneReached { get; set; } = true;
    
    /// <summary>
    /// Budget tracking streak notifications
    /// </summary>
    public bool StreakNotifications { get; set; } = false; // Opt-in by default
    
    // ===== METADATA =====
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // NOTE: Security-critical emails (password reset, MFA codes, security alerts, welcome email)
    // are NOT included in this entity because they cannot be disabled and always send
}
