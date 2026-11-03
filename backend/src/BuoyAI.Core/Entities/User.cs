using System.ComponentModel.DataAnnotations;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class User
{
  public int Id { get; set; }
  public string Username { get; set; } = string.Empty;
  public string PasswordHash { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  /// <summary>The user's own shareable referral code (lazily generated). Nullable until first use.</summary>
  public string? ReferralCode { get; set; }
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public string Currency { get; set; } = "USD";

  /// <summary>
  /// True once the user has explicitly chosen <see cref="Currency"/> (profile edit).
  /// While false, the first linked bank account may adopt its currency as the default;
  /// an explicit choice is never overridden by a later account link. (#941 step 2)
  /// </summary>
  public bool CurrencyIsUserSet { get; set; }

  /// <summary>
  /// ISO-3166 alpha-2 country the user signed up from, resolved by IP geolocation at
  /// registration. Null when geolocation was unavailable (local IPs, service outage).
  /// Feeds sanctions gating, tax-threshold segmentation, and per-market analytics. (#941)
  /// </summary>
  public string? SignupCountry { get; set; }

  /// <summary>
  /// Preferred UI/communication language (BCP-47, e.g. "es"). Null = English.
  /// Scaffold for the per-locale email/push layer; not yet user-editable. (#941)
  /// </summary>
  public string? PreferredLanguage { get; set; }

  /// <summary>
  /// First day of the user's budgeting week (0=Sunday..6=Saturday, .NET DayOfWeek).
  /// Null = legacy behavior (Weekly starts Sunday, BiWeekly anchors Monday), which
  /// existing users keep so their period boundaries never shift; new users default
  /// by signup country (US/CA→Sunday, else Monday) and can override in profile. (#941)
  /// </summary>
  public int? WeekStartDay { get; set; }
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }
  
  // Google OAuth fields
  public string? GoogleId { get; set; }
  public string? ProfilePictureUrl { get; set; }
  public bool IsGoogleUser { get; set; } = false;

  // Uploaded profile picture (stored in DB; small, client-resized). Distinct from
  // the Google ProfilePictureUrl. Null AvatarUpdatedAt = no custom avatar (fall back
  // to initials). Served via GET /api/user/avatar/{id}.
  public byte[]? AvatarImage { get; set; }
  public string? AvatarContentType { get; set; }
  public DateTime? AvatarUpdatedAt { get; set; }

  // Apple Sign-In fields
  public string? AppleId { get; set; }
  public bool IsAppleUser { get; set; } = false;
  // Apple refresh token (ENCRYPTED at rest via IEncryptionService), obtained by
  // exchanging the login authorization code. Used to revoke the Sign-in-with-Apple
  // grant when the account is deleted (App Store requirement). Null if never captured.
  public string? AppleRefreshToken { get; set; }

  // Data retention fields
  public bool IsDeleted { get; set; } = false;

  /// <summary>
  /// Self-host only: the first account created on a server, and any account an admin promotes later,
  /// is an admin. Hosted ignores it (hosted admins come from the Admin:Emails list).
  /// </summary>
  public bool IsInstanceAdmin { get; set; } = false;
  public DateTime? DeletedAt { get; set; }
  public string? DeletionReason { get; set; }

  // Multi-factor authentication fields
  public bool HasMfaEnabled { get; set; } = false;
  public MfaType MfaType { get; set; } = MfaType.None;
  public string? TotpSecret { get; set; } // Encrypted TOTP secret
  public DateTime? MfaEnabledAt { get; set; }
  public DateTime? MfaGracePeriodEndsAt { get; set; } // 30 days from first MFA setup
  public string? RecoveryCodes { get; set; } // JSON array of hashed recovery codes
  public bool HasPasskeyRegistered { get; set; } = false;

  // Subscription billing fields
  public SubscriptionTier? SubscriptionTier { get; set; } // Null = no active subscription
  public DateTime? SubscriptionStartDate { get; set; }
  public DateTime? SubscriptionEndDate { get; set; }
  
  // Trial fields
  public DateTime? TrialStartDate { get; set; }
  public DateTime? TrialEndDate { get; set; } // free trial (14 days by default, 30 with the website offer)
  public bool IsTrialActive { get; set; } = false;
  public bool HasUsedTrial { get; set; } = false; // Prevent multiple trials

  /// <summary>
  /// When the first connect-your-bank reminder (email, plus an in-app/push copy) was sent.
  /// NULL = not yet sent.
  /// The reminder is delivered by a periodic sweep, so this marker is what stops the sweep
  /// re-sending to the same user on every pass. Distinct from the trial-ENDING email, which
  /// Stripe's trial_will_end webhook drives near expiry.
  /// </summary>
  public DateTime? TrialReminderSentAt { get; set; }

  /// <summary>
  /// When the second connect-your-bank email (the day-4 "what stopped you?" note) was sent.
  /// NULL = not yet sent. Only considered once <see cref="TrialReminderSentAt"/> is set.
  /// </summary>
  public DateTime? TrialReminder2SentAt { get; set; }

  /// <summary>
  /// When we asked this user to invite someone. NULL = not yet asked.
  /// Sent once, ever — one ignored invite prompt is a non-event, a recurring one is a reason
  /// to unsubscribe. Set even when the user has opted out, so we do not re-evaluate them.
  /// </summary>
  public DateTime? ReferralInvitePromptedAt { get; set; }

  // Email verification. Confirms the address is real and reachable, and that our
  // email-delivered codes (the final MFA fallback) actually arrive. Required before
  // protected operations such as connecting a bank account. OAuth (Google/Apple)
  // sign-ups are considered verified because the provider already vetted the email.
  public bool IsEmailVerified { get; set; } = false;
  public DateTime? EmailVerifiedAt { get; set; }

  // Set true when an account is created for an email that already consumed its
  // lifetime free trial (e.g. delete + re-register). Such users get no new trial
  // and must choose and pay for a plan before using the app; the frontend shows a
  // blocking paywall while this is true and there is no active subscription. Cleared
  // when a paid subscription becomes active.
  public bool RequiresPaidPlan { get; set; } = false;

  /// <summary>
  /// Signed up on the Cloud portal (buoy-ai.com/cloud/) to buy add-ons for their own server. Such an account
  /// gets a Stripe customer (Cloud billing needs it) but no hosted trial and no hosted onboarding emails.
  /// Opening the hosted app later works like any account without a plan.
  /// </summary>
  public bool CloudOnly { get; set; } = false;
  
  // Stripe integration
  public string? StripeCustomerId { get; set; } // Created during trial, no payment method

  /// <summary>
  /// When this user's subscription state was last projected from Stripe.
  ///
  /// Stripe is the authority; the trial/tier columns here are a projection. This timestamp
  /// debounces the forced refresh and makes staleness measurable. NULL means never synced,
  /// which is the correct and expected state for trials granted before trials were
  /// Stripe-backed.
  /// </summary>
  public DateTime? LastStripeSyncAt { get; set; }

  // Family relationship
  public int? FamilyId { get; set; }

  // AI preferences
  /// <summary>
  /// How the AI should interact with the user in chat.
  /// Quick = use sensible defaults, still confirm writes.
  /// Clarifying = ask questions for ambiguous input, confirm writes.
  /// </summary>
  public AIInteractionMode AiInteractionMode { get; set; } = AIInteractionMode.Quick;

  /// <summary>
  /// The "AI Control Spectrum" (issue #412): how much the AI does autonomously.
  /// Manual = silent, Coach = insights only, Assistant = auto-categorize + suggest,
  /// Manager = act within guardrails. Defaults to Coach so existing users keep today's
  /// behavior (insights on, no auto-actions). Orthogonal to <see cref="AiInteractionMode"/>.
  /// </summary>
  public AIAutonomyLevel AiAutonomyLevel { get; set; } = AIAutonomyLevel.Coach;

  /// <summary>
  /// Whether the AI can remember facts learned from conversations.
  /// Opt-in only for privacy. When enabled, AI can remember dates, preferences, goals, etc.
  /// </summary>
  public bool AiMemoryEnabled { get; set; } = false;

  /// <summary>
  /// Whether to exclude pending transactions from budget calculations.
  /// When true, only posted/settled transactions count toward budget spending.
  /// Default is false (include pending in budget).
  /// </summary>
  public bool ExcludePendingFromBudget { get; set; } = false;

  /// <summary>
  /// When true, changing an account's budget association also reattributes that account's
  /// existing transactions to the new owner (the ones still tracking the old default; explicit
  /// claims to a different member are preserved). Default false = only future transactions move.
  /// </summary>
  public bool ReattributeOnAccountAssociationChange { get; set; } = false;

  /// <summary>
  /// JSON array of category identifiers that should trigger subscription tracking prompt.
  /// Built-in categories stored as string numbers (e.g., "13" for Subscriptions).
  /// Custom categories stored as "custom_123" format.
  /// Default: Subscriptions (13) and Insurance (12).
  /// </summary>
  public string SubscriptionPromptCategories { get; set; } = "[\"13\", \"12\"]";

  /// <summary>
  /// UTC timestamp of the last time on-demand/weekly subscription detection ran for this user.
  /// Authoritative source for the on-demand detection cooldown — reliable even when a run
  /// produced zero suggestions (inferring from suggestion DetectedAt lost that signal and let
  /// users hammer the Detect endpoint). Null = detection has never run.
  /// </summary>
  public DateTime? LastSubscriptionDetectionRunAt { get; set; }

  // Navigation properties
  public Family? Family { get; set; }
  public ICollection<FamilyMember> FamilyMemberships { get; set; } = new List<FamilyMember>();
  public ICollection<Account> Accounts { get; set; } = new List<Account>();
  public ICollection<Budget> Budgets { get; set; } = new List<Budget>();
  public ICollection<PlaidConnection> PlaidConnections { get; set; } = new List<PlaidConnection>();
  public ICollection<Subscription> Subscriptions { get; set; } = new List<Subscription>();
  public ICollection<UserSubscription> UserSubscriptions { get; set; } = new List<UserSubscription>(); // BuoyAI billing subscriptions
  public ICollection<AIInsight> AIInsights { get; set; } = new List<AIInsight>();
  public ICollection<AIRefreshUsage> AIRefreshUsages { get; set; } = new List<AIRefreshUsage>();
  public ICollection<TwoFactorCode> TwoFactorCodes { get; set; } = new List<TwoFactorCode>();
  public ICollection<UserPasskey> UserPasskeys { get; set; } = new List<UserPasskey>();
  // App Store 5.1.2(i) (Nov 2025): personal data may be shared with third-party AI only after
  // explicit permission. Null = not granted. Every model-calling surface (chat, insights,
  // categorization, chart generation, Wrapped garnish, background insight jobs) checks this.
  public DateTime? AiConsentGrantedAt { get; set; }
  // Explicit opt-out. Distinct from "never decided" (both null), though both block AI on
  // every platform now: consent is explicit everywhere (see AiConsent).
  public DateTime? AiConsentWithdrawnAt { get; set; }
  // Which disclosure the grant was given under (AiConsent.CurrentVersion). Null = before
  // versioning, which no longer counts, so those users see the fuller sheet once.
  public int? AiConsentVersion { get; set; }

  public ICollection<PasskeyChallenge> PasskeyChallenges { get; set; } = new List<PasskeyChallenge>();
  public ICollection<UserCategory> UserCategories { get; set; } = new List<UserCategory>();
  public ICollection<UserAiMemory> AiMemories { get; set; } = new List<UserAiMemory>();
  public ICollection<AiToolAuditLog> AiToolAuditLogs { get; set; } = new List<AiToolAuditLog>();
  public ICollection<PendingToolConfirmation> PendingToolConfirmations { get; set; } = new List<PendingToolConfirmation>();
}
