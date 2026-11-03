using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Infrastructure.Data;

public class BuoyAIDbContext : DbContext
{
  public BuoyAIDbContext(DbContextOptions<BuoyAIDbContext> options) : base(options)
  {
  }

  public override int SaveChanges()
  {
    SeedTransactionAttribution();
    BumpUpdatedAtTimestamps();
    return base.SaveChanges();
  }

  public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
  {
    SeedTransactionAttribution();
    BumpUpdatedAtTimestamps();
    return base.SaveChangesAsync(cancellationToken);
  }

  /// <summary>
  /// Centrally stamps <c>UpdatedAt = DateTime.UtcNow</c> on every entity in the Modified state
  /// that exposes a writable, mapped <c>DateTime</c> (or <c>DateTime?</c>) <c>UpdatedAt</c> property
  /// (#493). Before this, individual services had to remember to bump UpdatedAt by hand and several
  /// didn't (e.g. SavingsGoal's UpdatedAt was effectively frozen), which broke offline-sync conflict
  /// detection since a stale server timestamp never looked "newer" than the client's baseUpdatedAt.
  ///
  /// Uses <see cref="Microsoft.EntityFrameworkCore.Metadata.IEntityType.FindProperty(string)"/> (a
  /// dictionary lookup, so no per-save reflection cost) and only touches Added/Modified === Modified
  /// entries, so a pure navigation/owned-collection change on an otherwise-unmodified principal is
  /// left alone. Callers that still set UpdatedAt manually become redundant but harmless — this
  /// overwrites with the same "now". Provider-agnostic: works on the InMemory store used by tests.
  /// </summary>
  private void BumpUpdatedAtTimestamps()
  {
    var now = DateTime.UtcNow;
    foreach (var entry in ChangeTracker.Entries())
    {
      if (entry.State != EntityState.Modified)
        continue;

      var property = entry.Metadata.FindProperty("UpdatedAt");
      if (property == null || property.IsShadowProperty())
        continue;

      var clrType = property.ClrType;
      if (clrType != typeof(DateTime) && clrType != typeof(DateTime?))
        continue;

      entry.Property("UpdatedAt").CurrentValue = now;
    }
  }

  /// <summary>
  /// Snapshots budget attribution onto new transactions: a transaction inherits its account's
  /// AssociatedUserId as its ClaimedByUserId at creation (null = the family budget). This makes the
  /// account's "associated user" the default owner across every ingest path (Plaid sync, import,
  /// manual) without re-attributing history when an account's association later changes. Already-set
  /// ClaimedByUserId (an explicit claim) is left untouched.
  /// </summary>
  private void SeedTransactionAttribution()
  {
    var added = ChangeTracker.Entries<Transaction>()
      .Where(e => e.State == EntityState.Added && e.Entity.ClaimedByUserId == null)
      .Select(e => e.Entity)
      .ToList();
    if (added.Count == 0) return;

    // Prefer the tracked Account; DB-load associations only for transactions whose account isn't loaded.
    var needLookup = added.Where(t => t.Account == null).Select(t => t.AccountId).Distinct().ToList();
    var assocByAccount = needLookup.Count == 0
      ? new Dictionary<int, int?>()
      : Accounts.Where(a => needLookup.Contains(a.Id))
          .Select(a => new { a.Id, a.AssociatedUserId })
          .ToDictionary(x => x.Id, x => x.AssociatedUserId);

    foreach (var t in added)
    {
      t.ClaimedByUserId = t.Account?.AssociatedUserId
        ?? (assocByAccount.TryGetValue(t.AccountId, out var assoc) ? assoc : null);
    }
  }

  public DbSet<User> Users { get; set; }
  public DbSet<Family> Families { get; set; }
  public DbSet<FamilyMember> FamilyMembers { get; set; }
  public DbSet<FamilyInvitation> FamilyInvitations { get; set; }
  public DbSet<Account> Accounts { get; set; }
  public DbSet<Transaction> Transactions { get; set; }
  public DbSet<Budget> Budgets { get; set; }
  public DbSet<BudgetCategory> BudgetCategories { get; set; }
  public DbSet<BudgetShare> BudgetShares { get; set; }
  public DbSet<BudgetTransfer> BudgetTransfers { get; set; }
  public DbSet<BudgetPeriodSnapshot> BudgetPeriodSnapshots { get; set; }
  public DbSet<BudgetPeriodCategorySnapshot> BudgetPeriodCategorySnapshots { get; set; }
  public DbSet<CategoryGroup> CategoryGroups { get; set; }
  public DbSet<PlaidConnection> PlaidConnections { get; set; }
  public DbSet<Subscription> Subscriptions { get; set; }
  public DbSet<SubscriptionTransaction> SubscriptionTransactions { get; set; }
  public DbSet<SavingsGoal> SavingsGoals { get; set; }
  public DbSet<SavingsAllocation> SavingsAllocations { get; set; }
  public DbSet<Referral> Referrals { get; set; }
  public DbSet<AIInsight> AIInsights { get; set; }
  public DbSet<UserVisualization> UserVisualizations { get; set; }
  public DbSet<AIRefreshUsage> AIRefreshUsages { get; set; }
  public DbSet<AIUsageRecord> AIUsageRecords { get; set; }
  public DbSet<DataRetentionAuditLog> DataRetentionAuditLogs { get; set; }
  public DbSet<SecurityAuditLog> SecurityAuditLogs { get; set; }
  public DbSet<ErrorLog> ErrorLogs { get; set; }
  public DbSet<TwoFactorCode> TwoFactorCodes { get; set; }
  public DbSet<UserPasskey> UserPasskeys { get; set; }
  public DbSet<PasskeyChallenge> PasskeyChallenges { get; set; }
  public DbSet<UserConsent> UserConsents { get; set; }
  public DbSet<Notification> Notifications { get; set; }
  public DbSet<UserOnboardingQuiz> UserOnboardingQuizzes { get; set; }
  public DbSet<UserSubscription> UserSubscriptions { get; set; }
  public DbSet<SubscriptionPayment> SubscriptionPayments { get; set; }
  public DbSet<ProcessedStripeEvent> ProcessedStripeEvents { get; set; }
  public DbSet<ProcessedRevenueCatEvent> ProcessedRevenueCatEvents { get; set; }
  public DbSet<Reservation> Reservations { get; set; }
  public DbSet<UserEntitlement> UserEntitlements { get; set; }
  public DbSet<ReceiptScan> ReceiptScans { get; set; }
  public DbSet<SyncIdempotencyKey> SyncIdempotencyKeys { get; set; }
  public DbSet<RefreshToken> RefreshTokens { get; set; }

  // BuoyAI Connect — OAuth 2.1 authorization server (RFC #782)
  public DbSet<OAuthClient> OAuthClients { get; set; }
  public DbSet<OAuthAuthorizationRequest> OAuthAuthorizationRequests { get; set; }
  public DbSet<OAuthRefreshToken> OAuthRefreshTokens { get; set; }
  public DbSet<ConnectPersonalToken> ConnectPersonalTokens { get; set; }
  public DbSet<UserExternalLogin> UserExternalLogins { get; set; }
  public DbSet<OidcLoginRequest> OidcLoginRequests { get; set; }
  public DbSet<PasswordResetToken> PasswordResetTokens { get; set; }
  public DbSet<UserEmailPreferences> UserEmailPreferences { get; set; }
  public DbSet<Broadcast> Broadcasts { get; set; }
  public DbSet<BroadcastRecipient> BroadcastRecipients { get; set; }
  public DbSet<TrustedDevice> TrustedDevices { get; set; }
  public DbSet<LoginAttempt> LoginAttempts { get; set; }
  public DbSet<UserMfaPreferences> UserMfaPreferences { get; set; }
  public DbSet<UserDeviceToken> UserDeviceTokens { get; set; }
  public DbSet<RegistrationInvite> RegistrationInvites { get; set; }
  public DbSet<InstanceSetting> InstanceSettings { get; set; }
  public DbSet<CloudPairing> CloudPairings { get; set; }
  public DbSet<UserPushPreferences> UserPushPreferences { get; set; }
  public DbSet<QueuedPushNotification> QueuedPushNotifications { get; set; }
  public DbSet<UserCategory> UserCategories { get; set; }
  public DbSet<Category> Categories { get; set; }
  public DbSet<DismissedSubscriptionPattern> DismissedSubscriptionPatterns { get; set; }
  public DbSet<SuggestedSubscription> SuggestedSubscriptions { get; set; }
  public DbSet<CrowdsourcedSubscriptionPattern> CrowdsourcedSubscriptionPatterns { get; set; }
  public DbSet<AICategorizationQueueItem> AICategorizationQueue { get; set; }
  public DbSet<CategorizationRule> CategorizationRules { get; set; }
  
  // AI Conversation tracking
  public DbSet<AiConversation> AiConversations { get; set; }
  public DbSet<AiConversationMessage> AiConversationMessages { get; set; }

  public DbSet<AiToolExecution> AiToolExecutions { get; set; }
  public DbSet<AiActionFeedback> AiActionFeedback { get; set; }
  
  // Gamification
  public DbSet<UserAchievement> UserAchievements { get; set; }
  public DbSet<WrappedStatCutoff> WrappedStatCutoffs { get; set; }
  public DbSet<WrappedBonusCardCache> WrappedBonusCardCaches { get; set; }
  public DbSet<UserStreak> UserStreaks { get; set; }
  public DbSet<UserChallenge> UserChallenges { get; set; }
  
  // AI Memory & Security
  public DbSet<UserAiMemory> UserAiMemories { get; set; }
  public DbSet<AiToolAuditLog> AiToolAuditLogs { get; set; }
  public DbSet<PendingToolConfirmation> PendingToolConfirmations { get; set; }
  public DbSet<TrialHistory> TrialHistories { get; set; }
  public DbSet<MarketingLead> MarketingLeads { get; set; }

  /// <summary>
  /// Maps PostgreSQL's built-in <c>xmin</c> system column as an optimistic-concurrency token via a
  /// shadow <c>uint</c> property. Replicates the old Npgsql <c>UseXminAsConcurrencyToken()</c> helper
  /// (removed in EF Core 10): no real column is added — <c>xmin</c> already exists on every row and is
  /// bumped by Postgres on every UPDATE, so a stale read makes SaveChanges throw
  /// DbUpdateConcurrencyException. Non-Postgres providers (e.g. the in-memory test store) simply never
  /// populate it, so it stays a no-op there.
  /// </summary>
  private static void UseXminAsConcurrencyToken<TEntity>(EntityTypeBuilder<TEntity> entity)
    where TEntity : class
  {
    entity.Property<uint>("xmin")
      .HasColumnName("xmin")
      .HasColumnType("xid")
      .ValueGeneratedOnAddOrUpdate()
      .IsConcurrencyToken();
  }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    base.OnModelCreating(modelBuilder);

    // User entity configuration
    modelBuilder.Entity<User>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Username).IsRequired().HasMaxLength(50);
      entity.Property(e => e.PasswordHash).IsRequired();
      entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
      entity.Property(e => e.FirstName).HasMaxLength(100);
      entity.Property(e => e.LastName).HasMaxLength(100);
      entity.Property(e => e.Currency).HasMaxLength(3).HasDefaultValue("USD");
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      // AI Control Spectrum (#412): backfill existing users to Coach (not Manual=0) so
      // adding the column doesn't silence the AI for everyone. The sentinel is set to an
      // impossible enum value (-1) so that Manual (CLR default 0) is still treated as a real,
      // persistable choice rather than "unset" (otherwise EF would substitute the DB default).
      entity.Property(e => e.AiAutonomyLevel)
        .HasDefaultValue(BuoyAI.Core.Enums.AIAutonomyLevel.Coach)
        .HasSentinel((BuoyAI.Core.Enums.AIAutonomyLevel)(-1));

      entity.HasIndex(e => e.Username).IsUnique();
      entity.HasIndex(e => e.Email).IsUnique();
    });

    // Trial-abuse history: one row per email (hash) that has ever consumed a trial.
    // Survives account deletion so re-registration cannot reset the free trial.
    modelBuilder.Entity<TrialHistory>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.EmailHash).IsRequired().HasMaxLength(64);
      entity.Property(e => e.FirstSource).HasMaxLength(20);
      entity.HasIndex(e => e.EmailHash).IsUnique();
    });

    // Marketing lead capture: an email traded on the marketing site for a longer trial.
    // Distinct from TrialHistory — this one holds the plaintext address (we have to email
    // it) and is the promise we honour when that address later registers.
    modelBuilder.Entity<MarketingLead>(entity =>
    {
      entity.ToTable("marketing_leads");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.Email).HasColumnName("email").IsRequired().HasMaxLength(320);
      entity.Property(e => e.Source).HasColumnName("source").HasMaxLength(40);
      entity.Property(e => e.OfferedTrialDays).HasColumnName("offered_trial_days");
      entity.Property(e => e.RedeemedAt).HasColumnName("redeemed_at");
      entity.Property(e => e.RedeemedByUserId).HasColumnName("redeemed_by_user_id");
      entity.Property(e => e.CalculatorSnapshot).HasColumnName("calculator_snapshot");
      entity.Property(e => e.ConfirmationSentAt).HasColumnName("confirmation_sent_at");
      entity.Property(e => e.ReminderSentAt).HasColumnName("reminder_sent_at");
      entity.Property(e => e.UnsubscribeToken).HasColumnName("unsubscribe_token").IsRequired().HasMaxLength(64);
      entity.Property(e => e.UnsubscribedAt).HasColumnName("unsubscribed_at");
      entity.Property(e => e.CaptureCount).HasColumnName("capture_count").HasDefaultValue(1);
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => e.Email).IsUnique();
      entity.HasIndex(e => e.UnsubscribeToken).IsUnique();
    });

    // Family entity configuration
    modelBuilder.Entity<Family>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.CreatedByUser)
        .WithMany()
        .HasForeignKey(e => e.CreatedByUserId)
        .OnDelete(DeleteBehavior.Restrict);

      entity.HasIndex(e => e.CreatedByUserId);
    });

    // FamilyMember entity configuration
    modelBuilder.Entity<FamilyMember>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Role).IsRequired();
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.JoinedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Family)
        .WithMany(f => f.Members)
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.User)
        .WithMany(u => u.FamilyMemberships)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.FamilyId);
      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.FamilyId }).IsUnique();
    });

    // FamilyInvitation entity configuration
    modelBuilder.Entity<FamilyInvitation>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.InvitedEmail).IsRequired().HasMaxLength(255);
      entity.Property(e => e.InvitationToken).IsRequired().HasMaxLength(100);
      entity.Property(e => e.InvitedRole).IsRequired();
      entity.Property(e => e.IsAccepted).HasDefaultValue(false);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.InvitedByUser)
        .WithMany()
        .HasForeignKey(e => e.InvitedByUserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.InvitationToken).IsUnique();
      entity.HasIndex(e => e.FamilyId);
      entity.HasIndex(e => e.InvitedEmail);
      entity.HasIndex(e => e.ExpiresAt);
    });

    // User-Family relationship configuration
    modelBuilder.Entity<User>(entity =>
    {
      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.FamilyId);
    });

    // Account entity configuration
    modelBuilder.Entity<Account>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.PlaidAccountId).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
      entity.Property(e => e.OfficialName).HasMaxLength(100);
      entity.Property(e => e.Type).IsRequired();
      entity.Property(e => e.Subtype).HasMaxLength(50);
      entity.Property(e => e.CurrentBalance).HasColumnType("decimal(18,2)");
      entity.Property(e => e.AvailableBalance).HasColumnType("decimal(18,2)");
      entity.Property(e => e.Currency).HasMaxLength(3).HasDefaultValue("USD");
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(e => e.Accounts)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      // Account → associated user (whose budget it feeds by default). Optional; if that user is
      // deleted the account reverts to the family default rather than blocking the deletion.
      entity.HasOne(e => e.AssociatedUser)
        .WithMany()
        .HasForeignKey(e => e.AssociatedUserId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.PlaidAccountId).IsUnique();
    });

    // Transaction entity configuration
    modelBuilder.Entity<Transaction>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.PlaidTransactionId).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Amount).HasColumnType("decimal(18,2)").IsRequired();
      entity.Property(e => e.Type).IsRequired();
      entity.Property(e => e.Description).HasMaxLength(500);
      entity.Property(e => e.MerchantName).HasMaxLength(200);
      entity.Property(e => e.IsUserCategorized).HasDefaultValue(false);
      entity.Property(e => e.ImportBatchId).HasMaxLength(40);
      entity.Property(e => e.Date).IsRequired();
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Account)
        .WithMany(e => e.Transactions)
        .HasForeignKey(e => e.AccountId)
        .OnDelete(DeleteBehavior.Cascade);

      // Unified category reference
      entity.HasOne(e => e.CategoryNav)
        .WithMany(c => c.Transactions)
        .HasForeignKey(e => e.CategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      // AI suggested category reference
      entity.HasOne(e => e.AISuggestedCategoryNav)
        .WithMany()
        .HasForeignKey(e => e.AISuggestedCategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      // Personal sub-category for allowance owners
      entity.HasOne(e => e.PersonalCategoryNav)
        .WithMany()
        .HasForeignKey(e => e.PersonalUserCategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      // Budget attribution
      entity.HasOne(e => e.Budget)
        .WithMany()
        .HasForeignKey(e => e.BudgetId)
        .OnDelete(DeleteBehavior.SetNull);

      // User who claimed this transaction for personal tracking
      entity.HasOne(e => e.ClaimedByUser)
        .WithMany()
        .HasForeignKey(e => e.ClaimedByUserId)
        .OnDelete(DeleteBehavior.SetNull);

      // Transaction splitting - parent/child relationship
      entity.HasOne(e => e.ParentTransaction)
        .WithMany(e => e.SplitTransactions)
        .HasForeignKey(e => e.ParentTransactionId)
        .OnDelete(DeleteBehavior.Cascade);

      // Savings goal withdrawal
      entity.HasOne(e => e.SavingsGoal)
        .WithMany()
        .HasForeignKey(e => e.SavingsGoalId)
        .OnDelete(DeleteBehavior.SetNull);

      // Draw settlement: the transfer that covered an earmarked purchase (0039). Deleting the
      // transfer just puts the draw back in flight, it never deletes the purchase.
      entity.HasOne(e => e.CoveringTransfer)
        .WithMany()
        .HasForeignKey(e => e.CoveringTransferTransactionId)
        .OnDelete(DeleteBehavior.SetNull);
      entity.HasIndex(e => e.CoveringTransferTransactionId);

      entity.HasIndex(e => e.PlaidTransactionId).IsUnique();
      entity.HasIndex(e => e.ImportBatchId).HasFilter("\"ImportBatchId\" IS NOT NULL");
      entity.HasIndex(e => e.Date);
      entity.HasIndex(e => e.CategoryId);
      entity.HasIndex(e => e.BudgetId);
      entity.HasIndex(e => e.ClaimedByUserId);
      entity.HasIndex(e => e.ParentTransactionId);
      
      // Composite indexes for common query patterns
      entity.HasIndex(e => new { e.AccountId, e.Date }).HasDatabaseName("IX_Transactions_AccountId_Date");
      entity.HasIndex(e => new { e.CategoryId, e.Date }).HasDatabaseName("IX_Transactions_CategoryId_Date");
    });

    // Budget entity configuration
    modelBuilder.Entity<Budget>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Period).IsRequired();
      entity.Property(e => e.CurrentPeriodStart).IsRequired();
      entity.Property(e => e.CurrentPeriodEnd).IsRequired();
      entity.Property(e => e.LastReset).IsRequired();
      entity.Property(e => e.AutoReset).HasDefaultValue(true);
      entity.Property(e => e.TotalIncome).HasColumnType("decimal(18,2)");
      entity.Property(e => e.TotalExpenses).HasColumnType("decimal(18,2)");
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      
      // Hierarchical budget properties
      entity.Property(e => e.AllocatedAmount).HasColumnType("decimal(18,2)");
      entity.Property(e => e.Type).HasDefaultValue(BudgetType.Personal);
      entity.Property(e => e.IsPrimary).HasDefaultValue(false);
      
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(e => e.Budgets)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Family)
        .WithMany(f => f.Budgets)
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.SetNull);

      // Self-referential relationship for parent/child budgets
      entity.HasOne(e => e.ParentBudget)
        .WithMany(e => e.ChildBudgets)
        .HasForeignKey(e => e.ParentBudgetId)
        .OnDelete(DeleteBehavior.SetNull);

      // Child budget default savings goal
      entity.HasOne(e => e.DefaultSavingsGoal)
        .WithMany()
        .HasForeignKey(e => e.DefaultSavingsGoalId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.FamilyId);
      entity.HasIndex(e => e.ParentBudgetId);
      entity.HasIndex(e => e.Type);
      entity.HasIndex(e => e.IsPrimary);
      
      // Composite index for common query pattern: GetActiveBudgetsByTypeAsync
      entity.HasIndex(e => new { e.UserId, e.Type, e.IsActive }).HasDatabaseName("IX_Budgets_UserId_Type_IsActive");
    });

    // BudgetCategory entity configuration
    modelBuilder.Entity<BudgetCategory>(entity =>
    {
      entity.HasKey(e => e.Id);
      // Optimistic concurrency: use Postgres' built-in xmin system column as the concurrency token
      // so concurrent fund transfers can't both commit against a stale AllocatedAmount and
      // over-allocate. xmin is a system column — no schema migration or entity property required.
      UseXminAsConcurrencyToken(entity);
      entity.Property(e => e.AllocatedAmount).HasColumnType("decimal(18,2)");
      // Note: SpentAmount removed from entity - spending is now calculated from transactions
      entity.Property(e => e.SortOrder).HasDefaultValue(0);
      
      // Income vs expense tracking
      entity.Property(e => e.IsIncome).HasDefaultValue(false);
      entity.Property(e => e.DefaultAllocatedAmount).HasColumnType("decimal(18,2)");
      
      // Rollover functionality
      entity.Property(e => e.AllowRollover).HasDefaultValue(true);
      entity.Property(e => e.RolloverBalance).HasColumnType("decimal(18,2)").HasDefaultValue(0);
      entity.Property(e => e.MaxRolloverMultiplier).HasColumnType("decimal(5,2)");
      
      // Count-up budgeting fields
      entity.Property(e => e.IsCountUpEnabled).HasDefaultValue(false);
      entity.Property(e => e.DailyAllowance).HasColumnType("decimal(18,2)").HasDefaultValue(0);
      entity.Property(e => e.LastAllowanceCalculation).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.EarnedToDate).HasColumnType("decimal(18,2)").HasDefaultValue(0);
      
      // Allowance support
      entity.Property(e => e.IsAllowance).HasDefaultValue(false);
      
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Budget)
        .WithMany(e => e.Categories)
        .HasForeignKey(e => e.BudgetId)
        .OnDelete(DeleteBehavior.Cascade);

      // Link to category group
      entity.HasOne(e => e.Group)
        .WithMany()
        .HasForeignKey(e => e.GroupId)
        .OnDelete(DeleteBehavior.SetNull);

      // Unified category reference
      entity.HasOne(e => e.CategoryNav)
        .WithMany(c => c.BudgetCategories)
        .HasForeignKey(e => e.CategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      // Link to allowance budget this category funds
      entity.HasOne(e => e.LinkedBudget)
        .WithMany()
        .HasForeignKey(e => e.LinkedBudgetId)
        .OnDelete(DeleteBehavior.SetNull);

      // User who receives this allowance
      entity.HasOne(e => e.AssignedUser)
        .WithMany()
        .HasForeignKey(e => e.AssignedUserId)
        .OnDelete(DeleteBehavior.SetNull);

      // Savings goal for rollover overflow
      entity.HasOne(e => e.RolloverOverflowSavingsGoal)
        .WithMany()
        .HasForeignKey(e => e.RolloverOverflowSavingsGoalId)
        .OnDelete(DeleteBehavior.SetNull);

      // Category-level savings goal for auto-transfer at period end
      entity.HasOne(e => e.SavingsGoal)
        .WithMany()
        .HasForeignKey(e => e.SavingsGoalId)
        .OnDelete(DeleteBehavior.SetNull);
      
      entity.HasIndex(e => e.SavingsGoalId);
      entity.HasIndex(e => e.CategoryId);

      // Unique constraint: Budget + CategoryId + AssignedUserId
      // This allows the same category to exist multiple times in a budget if assigned to different users (allowances)
      entity.HasIndex(e => new { e.BudgetId, e.CategoryId, e.AssignedUserId }).IsUnique();
      entity.HasIndex(e => e.LinkedBudgetId);
      entity.HasIndex(e => e.AssignedUserId);
      entity.HasIndex(e => e.IsAllowance);
      entity.HasIndex(e => e.GroupId);
    });

    // CategoryGroup entity configuration - for organizing budget categories
    modelBuilder.Entity<CategoryGroup>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
      entity.Property(e => e.Icon).HasMaxLength(10);
      entity.Property(e => e.SortOrder).HasDefaultValue(0);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.FamilyId);
      entity.HasIndex(e => e.SortOrder);
    });

    // BudgetShare entity configuration - for custom budget visibility
    modelBuilder.Entity<BudgetShare>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.CanEdit).HasDefaultValue(false);
      entity.Property(e => e.CanViewTransactions).HasDefaultValue(false);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Budget)
        .WithMany(b => b.Shares)
        .HasForeignKey(e => e.BudgetId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.SharedWithUser)
        .WithMany()
        .HasForeignKey(e => e.SharedWithUserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.BudgetId);
      entity.HasIndex(e => e.SharedWithUserId);
      entity.HasIndex(e => new { e.BudgetId, e.SharedWithUserId }).IsUnique();
    });

    // BudgetTransfer entity configuration - for tracking fund transfers between categories
    modelBuilder.Entity<BudgetTransfer>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Amount).HasPrecision(18, 2);
      entity.Property(e => e.Note).HasMaxLength(500);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Budget)
        .WithMany()
        .HasForeignKey(e => e.BudgetId)
        .OnDelete(DeleteBehavior.Cascade);

      // SET NULL (not RESTRICT): mirrors the DB constraints installed by migration 0013 so a budget
      // with fund-transfer history can be edited/deleted without a 500. The category-id columns are
      // nullable and every transfer read tolerates a null category, so the financial audit row
      // survives while the category delete succeeds. Leaving these as Restrict made the EF model
      // disagree with the shipped schema and re-broke budget-edit-with-transfers whenever a tracked
      // transfer was in the change tracker at SaveChanges.
      entity.HasOne(e => e.SourceCategory)
        .WithMany()
        .HasForeignKey(e => e.SourceCategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.DestinationCategory)
        .WithMany()
        .HasForeignKey(e => e.DestinationCategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      // SET NULL (not RESTRICT): deleting a savings goal that was ever a transfer source/destination
      // must not 500 on an FK violation (the goal became permanently undeletable). The goal-id columns
      // are nullable and transfer reads already tolerate a null goal, so the transfer's financial audit
      // row survives while the goal delete succeeds. Mirrors the transfer->category FKs (migration 0013).
      entity.HasOne(e => e.SourceSavingsGoal)
        .WithMany()
        .HasForeignKey(e => e.SourceSavingsGoalId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.DestinationSavingsGoal)
        .WithMany()
        .HasForeignKey(e => e.DestinationSavingsGoalId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.CreatedByUser)
        .WithMany()
        .HasForeignKey(e => e.CreatedByUserId)
        .OnDelete(DeleteBehavior.Restrict);

      entity.HasIndex(e => e.BudgetId);
      entity.HasIndex(e => e.CreatedAt);
    });

    // BudgetPeriodSnapshot entity configuration - for historical budget periods
    modelBuilder.Entity<BudgetPeriodSnapshot>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.TotalAllocated).HasColumnType("decimal(18,2)");
      entity.Property(e => e.TotalSpent).HasColumnType("decimal(18,2)");
      entity.Property(e => e.TotalIncome).HasColumnType("decimal(18,2)");
      entity.Property(e => e.TotalRolloverIn).HasColumnType("decimal(18,2)");
      entity.Property(e => e.TotalRolloverOut).HasColumnType("decimal(18,2)");
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Budget)
        .WithMany()
        .HasForeignKey(e => e.BudgetId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.BudgetId);
      entity.HasIndex(e => new { e.BudgetId, e.PeriodStart }).IsUnique();
      entity.HasIndex(e => e.PeriodStart);
    });

    // BudgetPeriodCategorySnapshot entity configuration - category state at period end
    modelBuilder.Entity<BudgetPeriodCategorySnapshot>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.CategoryName).HasMaxLength(100).IsRequired();
      entity.Property(e => e.AllocatedAmount).HasColumnType("decimal(18,2)");
      entity.Property(e => e.SpentAmount).HasColumnType("decimal(18,2)");
      entity.Property(e => e.RolloverIn).HasColumnType("decimal(18,2)");
      entity.Property(e => e.RolloverOut).HasColumnType("decimal(18,2)");

      entity.HasOne(e => e.BudgetPeriodSnapshot)
        .WithMany(s => s.CategorySnapshots)
        .HasForeignKey(e => e.BudgetPeriodSnapshotId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.BudgetCategory)
        .WithMany()
        .HasForeignKey(e => e.BudgetCategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.BudgetPeriodSnapshotId);
      entity.HasIndex(e => e.BudgetCategoryId);
    });

    // PlaidConnection entity configuration
    modelBuilder.Entity<PlaidConnection>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.AccessToken).IsRequired(); // Will be encrypted
      entity.Property(e => e.ItemId).IsRequired().HasMaxLength(100);
      entity.Property(e => e.InstitutionId).HasMaxLength(100);
      entity.Property(e => e.InstitutionName).HasMaxLength(200);
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.LastSyncAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(e => e.PlaidConnections)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.ItemId).IsUnique();
    });

    // Subscription entity configuration
    modelBuilder.Entity<Subscription>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(255);
      entity.Property(e => e.Description).HasMaxLength(1000);
      entity.Property(e => e.Amount).HasColumnType("decimal(10,2)").IsRequired();
      entity.Property(e => e.Frequency).IsRequired();
      entity.Property(e => e.NextDueDate).IsRequired();
      entity.Property(e => e.LastBilledDate);
      entity.Property(e => e.Status).IsRequired().HasDefaultValue(SubscriptionStatus.Active);
      entity.Property(e => e.Category)
        .IsRequired()
        .HasDefaultValue(SubscriptionCategory.Other)
        .HasSentinel((SubscriptionCategory)0); // Use DB default when value is 0 (invalid enum)
      entity.Property(e => e.IsDetected).IsRequired().HasDefaultValue(false);
      entity.Property(e => e.MerchantName).HasMaxLength(255);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(e => e.Subscriptions)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.LinkedTransaction)
        .WithMany()
        .HasForeignKey(e => e.LinkedTransactionId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.NextDueDate);
      entity.HasIndex(e => e.MerchantName);
    });

    // SubscriptionTransaction entity configuration
    modelBuilder.Entity<SubscriptionTransaction>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Subscription)
        .WithMany(e => e.SubscriptionTransactions)
        .HasForeignKey(e => e.SubscriptionId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Transaction)
        .WithMany()
        .HasForeignKey(e => e.TransactionId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => new { e.SubscriptionId, e.TransactionId }).IsUnique();
    });

    // DismissedSubscriptionPattern entity configuration
    modelBuilder.Entity<DismissedSubscriptionPattern>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.MerchantName).IsRequired().HasMaxLength(255);
      entity.Property(e => e.AmountMin).HasColumnType("decimal(10,2)");
      entity.Property(e => e.AmountMax).HasColumnType("decimal(10,2)");
      entity.Property(e => e.DismissedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.DismissReason).HasMaxLength(500);

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => new { e.UserId, e.MerchantName });
    });

    // SuggestedSubscription entity configuration
    modelBuilder.Entity<SuggestedSubscription>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.NormalizedMerchantName).IsRequired().HasMaxLength(255);
      entity.Property(e => e.DisplayMerchantName).IsRequired().HasMaxLength(255);
      entity.Property(e => e.Amount).HasColumnType("decimal(10,2)").IsRequired();
      entity.Property(e => e.Confidence).IsRequired();
      entity.Property(e => e.BaseConfidence).IsRequired();
      entity.Property(e => e.CrowdsourceBoost).HasDefaultValue(0);
      entity.Property(e => e.Status).IsRequired().HasDefaultValue(SuggestedSubscriptionStatus.Pending);
      entity.Property(e => e.HiddenFromReadOnly).HasDefaultValue(false);
      entity.Property(e => e.AIInsight).HasMaxLength(1000);
      entity.Property(e => e.DismissReason).HasMaxLength(500);
      entity.Property(e => e.MatchedTransactionIds).HasMaxLength(4000);
      entity.Property(e => e.DetectedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.CrowdsourcePattern)
        .WithMany(p => p.SuggestedSubscriptions)
        .HasForeignKey(e => e.CrowdsourcePatternId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasOne(e => e.ConfirmedSubscription)
        .WithMany()
        .HasForeignKey(e => e.ConfirmedSubscriptionId)
        .OnDelete(DeleteBehavior.SetNull);

      // Indexes for efficient querying
      entity.HasIndex(e => new { e.UserId, e.Status }); // Find pending suggestions for user
      entity.HasIndex(e => new { e.UserId, e.NormalizedMerchantName }); // Check if already suggested
      entity.HasIndex(e => e.SnoozeUntil).HasFilter("\"SnoozeUntil\" IS NOT NULL"); // Find snoozed items to un-snooze
    });

    // CrowdsourcedSubscriptionPattern entity configuration
    modelBuilder.Entity<CrowdsourcedSubscriptionPattern>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.NormalizedMerchantName).IsRequired().HasMaxLength(255);
      entity.Property(e => e.TypicalAmountMin).HasColumnType("decimal(10,2)").IsRequired();
      entity.Property(e => e.TypicalAmountMax).HasColumnType("decimal(10,2)").IsRequired();
      entity.Property(e => e.TypicalFrequency).IsRequired();
      entity.Property(e => e.ConfirmationCount).HasDefaultValue(0);
      entity.Property(e => e.DismissalCount).HasDefaultValue(0);
      entity.Property(e => e.ConfidenceBoost).HasDefaultValue(0);
      entity.Property(e => e.Version).HasDefaultValue(1).IsConcurrencyToken();
      entity.Property(e => e.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");

      // Unique index on normalized merchant name - only one pattern per merchant
      entity.HasIndex(e => e.NormalizedMerchantName).IsUnique();
    });

    // AICategorizationQueueItem entity configuration
    modelBuilder.Entity<AICategorizationQueueItem>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.QueuedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.ErrorMessage).HasMaxLength(1000);

      entity.HasOne(e => e.Transaction)
        .WithMany()
        .HasForeignKey(e => e.TransactionId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      // Index for efficient queue processing: pending items by user, ordered by queue time
      entity.HasIndex(e => new { e.Status, e.UserId, e.QueuedAt });
      
      // Index for checking if transaction is already queued
      entity.HasIndex(e => e.TransactionId);
    });

    // SavingsGoal entity configuration
    modelBuilder.Entity<SavingsGoal>(entity =>
    {
      entity.HasKey(e => e.Id);
      // Optimistic concurrency: guard CurrentAmount against lost updates when two transfers
      // (or a transfer and a deficit pull) touch the same goal at once. Uses Postgres' xmin
      // system column — no schema migration or entity property required.
      UseXminAsConcurrencyToken(entity);
      entity.Property(e => e.Type).HasDefaultValue(SavingsGoalType.Personal);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Description).HasMaxLength(500);
      entity.Property(e => e.TargetAmount).HasColumnType("decimal(18,2)").IsRequired();
      entity.Property(e => e.CurrentAmount).HasColumnType("decimal(18,2)").HasDefaultValue(0);
      entity.Property(e => e.AllocationPercentage).HasColumnType("decimal(5,2)").IsRequired();
      entity.Property(e => e.TargetDate); // Optional field
      entity.Property(e => e.IconName).HasMaxLength(50).HasDefaultValue("target");
      entity.Property(e => e.ColorTheme).HasMaxLength(20).HasDefaultValue("primary");
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.IsCompleted).HasDefaultValue(false);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.FamilyId);
      entity.HasIndex(e => e.Type);
      entity.HasIndex(e => e.IsActive);
      entity.HasIndex(e => e.TargetDate);
    });

    // SavingsAllocation entity configuration
    modelBuilder.Entity<SavingsAllocation>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.AllocatedAmount).HasColumnType("decimal(18,2)").IsRequired();
      entity.Property(e => e.LeftoverBudget).HasColumnType("decimal(18,2)").IsRequired();
      entity.Property(e => e.AllocationDate).IsRequired();
      entity.Property(e => e.Source).HasMaxLength(50).HasDefaultValue("budget_surplus");
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.SavingsGoal)
        .WithMany(e => e.Allocations)
        .HasForeignKey(e => e.SavingsGoalId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Budget)
        .WithMany()
        .HasForeignKey(e => e.BudgetId)
        .IsRequired(false)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.AllocationDate);
      entity.HasIndex(e => e.SavingsGoalId);
    });

    // Referral entity configuration
    modelBuilder.Entity<Referral>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.ReferralCode).IsRequired().HasMaxLength(16);
      entity.Property(e => e.RewardStatus).IsRequired().HasMaxLength(20).HasDefaultValue("pending");
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      // One row per referred user — a user can only be referred once.
      entity.HasIndex(e => e.RefereeUserId).IsUnique();
      entity.HasIndex(e => e.ReferrerUserId);
      entity.HasOne(e => e.Referrer).WithMany().HasForeignKey(e => e.ReferrerUserId)
        .OnDelete(DeleteBehavior.Cascade);
      entity.HasOne(e => e.Referee).WithMany().HasForeignKey(e => e.RefereeUserId)
        .OnDelete(DeleteBehavior.Cascade);
    });

    // Stripe webhook idempotency ledger — keyed on the Stripe event id.
    modelBuilder.Entity<ProcessedStripeEvent>(entity =>
    {
      entity.HasKey(e => e.EventId);
      entity.Property(e => e.EventId).HasMaxLength(255);
      entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
      entity.Property(e => e.ProcessedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
    });

    // Founding-member reservations (0046).
    modelBuilder.Entity<Reservation>(entity =>
    {
      entity.ToTable("Reservations");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Email).HasMaxLength(320);
      entity.Property(e => e.StripeCheckoutSessionId).IsRequired().HasMaxLength(255);
      entity.Property(e => e.StripeCustomerId).HasMaxLength(255);
      entity.Property(e => e.StripePaymentIntentId).HasMaxLength(255);
      entity.Property(e => e.Currency).IsRequired().HasMaxLength(3).HasDefaultValue("usd");
      entity.Property(e => e.PlanChoice).IsRequired().HasMaxLength(30).HasDefaultValue("unsure");
      entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("paid");
      entity.Property(e => e.Source).HasMaxLength(100);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
      entity.HasIndex(e => e.StripeCheckoutSessionId).IsUnique();
      entity.HasIndex(e => e.StripePaymentIntentId);
    });

    // Product entitlements projected from the billing provider (0052).
    modelBuilder.Entity<UserEntitlement>(entity =>
    {
      entity.ToTable("UserEntitlements");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Entitlement).IsRequired().HasMaxLength(50);
      entity.Property(e => e.ExternalId).IsRequired().HasMaxLength(255);
      entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue(EntitlementStatus.Active);
      entity.Property(e => e.Quantity).HasDefaultValue(1);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => new { e.UserId, e.Entitlement, e.Provider, e.ExternalId }).IsUnique();
      entity.HasIndex(e => new { e.Provider, e.ExternalId });
      entity.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
    });

    // RevenueCat webhook idempotency ledger (0040), same shape as the Stripe one.
    modelBuilder.Entity<ProcessedRevenueCatEvent>(entity =>
    {
      entity.HasKey(e => e.EventId);
      entity.Property(e => e.EventId).HasMaxLength(255);
      entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
      entity.Property(e => e.ProcessedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
    });

    // Receipt scanning (0045): receipts users sent in, and the splits offered for them.
    modelBuilder.Entity<ReceiptScan>(entity =>
    {
      entity.HasKey(e => e.Id);
      // Concurrency token: an update only lands if the status is still what was read, so two
      // Applies (two devices, a retry) can't both claim the same receipt.
      entity.Property(e => e.Status).IsRequired().HasMaxLength(30).IsConcurrencyToken();
      entity.Property(e => e.Retailer).HasMaxLength(60);
      entity.Property(e => e.OrderNumber).HasMaxLength(80);
      entity.Property(e => e.Total).HasPrecision(18, 2);
      entity.Property(e => e.ParsedJson).HasColumnType("jsonb");
      entity.Property(e => e.ProposalJson).HasColumnType("jsonb");
      entity.Property(e => e.Error).HasMaxLength(500);
      entity.HasIndex(e => new { e.UserId, e.Status });
      entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
    });

    // Offline-sync idempotency ledger (#493) — composite key (user_id, key). Mapped to the
    // snake_case table/columns created by migration 0019 (matches the sync contract), unlike the
    // PascalCase EF-legacy tables. DbUp owns the schema; this mapping just has to agree with it.
    modelBuilder.Entity<SyncIdempotencyKey>(entity =>
    {
      entity.ToTable("sync_idempotency_keys");
      entity.HasKey(e => new { e.UserId, e.Key });
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.Key).HasColumnName("key");
      entity.Property(e => e.OperationType).HasColumnName("operation_type").IsRequired();
      entity.Property(e => e.Status).HasColumnName("status").IsRequired();
      entity.Property(e => e.ProcessedAt).HasColumnName("processed_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => e.ProcessedAt);
    });

    // Refresh tokens — rotation lineage keyed on a unique token hash.
    modelBuilder.Entity<RefreshToken>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(128);
      entity.Property(e => e.ReplacedByTokenHash).HasMaxLength(128);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => e.TokenHash).IsUnique();
      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.Family);
      entity.Ignore(e => e.IsActive);
      entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);
    });

    // BuoyAI Connect (RFC #782) — OAuth 2.1 authorization-server tables. Mapped to the snake_case
    // schema created by migration 0020 (matches 0019's style); DbUp owns the schema, these mappings
    // just agree with it. All Guid/timestamps are set in code so the InMemory test store behaves.
    modelBuilder.Entity<OAuthClient>(entity =>
    {
      entity.ToTable("oauth_clients");
      entity.HasKey(e => e.ClientId);
      entity.Property(e => e.ClientId).HasColumnName("client_id");
      entity.Property(e => e.ClientName).HasColumnName("client_name").IsRequired();
      entity.Property(e => e.ClientUri).HasColumnName("client_uri");
      entity.Property(e => e.LogoUri).HasColumnName("logo_uri");
      entity.Property(e => e.RedirectUris).HasColumnName("redirect_uris").IsRequired();
      entity.Property(e => e.IsCimd).HasColumnName("is_cimd").HasDefaultValue(false);
      entity.Property(e => e.CimdFetchedAt).HasColumnName("cimd_fetched_at");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
    });

    modelBuilder.Entity<OAuthAuthorizationRequest>(entity =>
    {
      entity.ToTable("oauth_authorization_requests");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.RedirectUri).HasColumnName("redirect_uri").IsRequired();
      entity.Property(e => e.Scope).HasColumnName("scope").IsRequired();
      entity.Property(e => e.State).HasColumnName("state");
      entity.Property(e => e.CodeChallenge).HasColumnName("code_challenge").IsRequired();
      entity.Property(e => e.CodeChallengeMethod).HasColumnName("code_challenge_method").IsRequired();
      entity.Property(e => e.Resource).HasColumnName("resource");
      entity.Property(e => e.Code).HasColumnName("code");
      entity.Property(e => e.CodeExpiresAt).HasColumnName("code_expires_at");
      entity.Property(e => e.Consumed).HasColumnName("consumed").HasDefaultValue(false);
      entity.Property(e => e.Approved).HasColumnName("approved");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
      entity.HasIndex(e => e.Code);
    });

    modelBuilder.Entity<OAuthRefreshToken>(entity =>
    {
      entity.ToTable("oauth_refresh_tokens");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.ClientId).HasColumnName("client_id").IsRequired();
      entity.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired();
      entity.Property(e => e.Scope).HasColumnName("scope").IsRequired();
      entity.Property(e => e.Resource).HasColumnName("resource");
      entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
      entity.Property(e => e.ReplacedBy).HasColumnName("replaced_by");
      entity.Ignore(e => e.IsActive);
      entity.HasIndex(e => e.TokenHash);
      entity.HasIndex(e => new { e.UserId, e.ClientId });
    });

    // Connect personal access tokens (self-host) — snake_case table owned by DbUp migration 0059.
    modelBuilder.Entity<ConnectPersonalToken>(entity =>
    {
      entity.ToTable("connect_personal_tokens");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.Name).HasColumnName("name").IsRequired();
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
      entity.Property(e => e.LastUsedAt).HasColumnName("last_used_at");
      entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
      entity.Ignore(e => e.IsActive);
      entity.HasIndex(e => e.UserId);
    });

    // Single sign-on (self-host OpenID Connect): snake_case tables owned by DbUp migration 0060.
    modelBuilder.Entity<UserExternalLogin>(entity =>
    {
      entity.ToTable("user_external_logins");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.Issuer).HasColumnName("issuer").IsRequired();
      entity.Property(e => e.Subject).HasColumnName("subject").IsRequired();
      entity.Property(e => e.Email).HasColumnName("email");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
      entity.HasIndex(e => new { e.Issuer, e.Subject }).IsUnique();
      entity.HasIndex(e => e.UserId);
    });

    modelBuilder.Entity<OidcLoginRequest>(entity =>
    {
      entity.ToTable("oidc_login_requests");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.Kind).HasColumnName("kind").IsRequired();
      entity.Property(e => e.SecretHash).HasColumnName("secret_hash").IsRequired();
      entity.Property(e => e.Platform).HasColumnName("platform").IsRequired();
      entity.Property(e => e.Nonce).HasColumnName("nonce");
      entity.Property(e => e.CodeVerifier).HasColumnName("code_verifier");
      entity.Property(e => e.ReturnPath).HasColumnName("return_path");
      entity.Property(e => e.InviteCode).HasColumnName("invite_code");
      entity.Property(e => e.ClientChallenge).HasColumnName("client_challenge");
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.IsNewUser).HasColumnName("is_new_user");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
      entity.HasIndex(e => e.SecretHash).IsUnique();
      entity.HasIndex(e => e.ExpiresAt);
    });

    // Push notifications (#190) — snake_case tables owned by DbUp migration 0021.
    modelBuilder.Entity<UserDeviceToken>(entity =>
    {
      entity.ToTable("user_device_tokens");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.Token).HasColumnName("token").IsRequired();
      entity.Property(e => e.Platform).HasColumnName("platform").IsRequired();
      entity.Property(e => e.PublicKey).HasColumnName("public_key");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => e.Token).IsUnique();
      entity.HasIndex(e => e.UserId);
    });

    // Self-host sign-in (task 09) — snake_case tables owned by DbUp migration 0049.
    modelBuilder.Entity<RegistrationInvite>(entity =>
    {
      entity.ToTable("registration_invites");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.CodeHash).HasColumnName("code_hash").IsRequired();
      entity.Property(e => e.Note).HasColumnName("note");
      entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
      entity.Property(e => e.UsedByUserId).HasColumnName("used_by_user_id");
      // Concurrency token: spending an invite is UPDATE ... WHERE used_at IS NULL, so two sign-ups
      // with the same code cannot both win.
      entity.Property(e => e.UsedAt).HasColumnName("used_at").IsConcurrencyToken();
      entity.HasIndex(e => e.CodeHash).IsUnique();
    });

    modelBuilder.Entity<InstanceSetting>(entity =>
    {
      entity.ToTable("instance_settings");
      entity.HasKey(e => e.Key);
      entity.Property(e => e.Key).HasColumnName("key");
      entity.Property(e => e.Value).HasColumnName("value").IsRequired();
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
    });

    modelBuilder.Entity<CloudPairing>(entity =>
    {
      entity.ToTable("cloud_pairings");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
      entity.Property(e => e.CloudUrl).HasColumnName("cloud_url").IsRequired();
      entity.Property(e => e.ServerId).HasColumnName("server_id");
      entity.Property(e => e.Name).HasColumnName("name").IsRequired();
      entity.Property(e => e.CredentialCipher).HasColumnName("credential_cipher").IsRequired();
      entity.Property(e => e.EncryptionKeyCipher).HasColumnName("encryption_key_cipher").IsRequired();
      entity.Property(e => e.SigningKeyCipher).HasColumnName("signing_key_cipher").IsRequired();
      entity.Property(e => e.PairedAt).HasColumnName("paired_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
    });

    modelBuilder.Entity<UserPushPreferences>(entity =>
    {
      entity.ToTable("user_push_preferences");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.PushEnabled).HasColumnName("push_enabled").HasDefaultValue(true);
      entity.Property(e => e.ConnectionIssues).HasColumnName("connection_issues").HasDefaultValue(true);
      entity.Property(e => e.BudgetAlerts).HasColumnName("budget_alerts").HasDefaultValue(true);
      entity.Property(e => e.BillReminders).HasColumnName("bill_reminders").HasDefaultValue(true);
      entity.Property(e => e.SubscriptionDetection).HasColumnName("subscription_detection").HasDefaultValue(true);
      entity.Property(e => e.TransactionReview).HasColumnName("transaction_review").HasDefaultValue(true);
      entity.Property(e => e.QuietHoursEnabled).HasColumnName("quiet_hours_enabled").HasDefaultValue(false);
      entity.Property(e => e.QuietHoursStartMinutes).HasColumnName("quiet_hours_start_minutes").HasDefaultValue(1320);
      entity.Property(e => e.QuietHoursEndMinutes).HasColumnName("quiet_hours_end_minutes").HasDefaultValue(420);
      entity.Property(e => e.TimeZoneOffsetMinutes).HasColumnName("time_zone_offset_minutes").HasDefaultValue(0);
      entity.Property(e => e.TimeZoneId).HasColumnName("time_zone_id");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => e.UserId).IsUnique();
    });

    modelBuilder.Entity<QueuedPushNotification>(entity =>
    {
      entity.ToTable("queued_push_notifications");
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Id).HasColumnName("id");
      entity.Property(e => e.UserId).HasColumnName("user_id");
      entity.Property(e => e.Title).HasColumnName("title").IsRequired();
      entity.Property(e => e.Body).HasColumnName("body").IsRequired();
      entity.Property(e => e.DataJson).HasColumnName("data_json").IsRequired();
      entity.Property(e => e.DeliverAfter).HasColumnName("deliver_after");
      entity.Property(e => e.Attempts).HasColumnName("attempts").HasDefaultValue(0);
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => e.DeliverAfter);
    });

    // AIInsight entity configuration
    modelBuilder.Entity<AIInsight>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.GeneratedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      
      entity.HasOne(e => e.User)
        .WithMany(u => u.AIInsights)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.Context);
      entity.HasIndex(e => e.GeneratedAt);
      entity.HasIndex(e => e.IsDismissed);
      entity.HasIndex(e => e.IsArchived);
      entity.HasIndex(e => new { e.UserId, e.Context, e.GeneratedAt });
    });

    // UserVisualization entity configuration
    modelBuilder.Entity<UserVisualization>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.GeneratedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      
      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.Category);
      entity.HasIndex(e => e.GeneratedAt);
      entity.HasIndex(e => e.IsStarred);
      entity.HasIndex(e => e.IsDismissed);
      entity.HasIndex(e => new { e.UserId, e.IsStarred });
      entity.HasIndex(e => new { e.UserId, e.Category, e.GeneratedAt });
    });

    // AIRefreshUsage entity configuration
    modelBuilder.Entity<AIRefreshUsage>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.Date).HasColumnType("date");
      
      entity.HasOne(e => e.User)
        .WithMany(u => u.AIRefreshUsages)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.Date);
      entity.HasIndex(e => new { e.UserId, e.Context, e.Date }).IsUnique();
    });

    // TwoFactorCode entity configuration
    modelBuilder.Entity<TwoFactorCode>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Code).IsRequired().HasMaxLength(10);
      entity.Property(e => e.Purpose).IsRequired().HasMaxLength(50);
      entity.Property(e => e.ExpiresAt).IsRequired();
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.IsUsed).HasDefaultValue(false);
      entity.Property(e => e.IpAddress).HasMaxLength(45); // IPv6 max length
      entity.Property(e => e.UserAgent).HasMaxLength(500);

      entity.HasOne(e => e.User)
        .WithMany(e => e.TwoFactorCodes)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.ExpiresAt);
      entity.HasIndex(e => new { e.UserId, e.Purpose, e.IsUsed });
    });

    // UserConsent entity configuration
    modelBuilder.Entity<UserConsent>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.ConsentType).IsRequired().HasMaxLength(100);
      entity.Property(e => e.ConsentVersion).IsRequired().HasMaxLength(50);
      entity.Property(e => e.HasConsented).IsRequired();
      entity.Property(e => e.ConsentedAt).IsRequired();
      entity.Property(e => e.ConsentText).HasMaxLength(2000);
      entity.Property(e => e.IpAddress).HasMaxLength(45); // IPv6 max length
      entity.Property(e => e.UserAgent).HasMaxLength(500);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.ConsentType });
      entity.HasIndex(e => e.ConsentedAt);
    });

    // Notification entity configuration
    modelBuilder.Entity<Notification>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
      entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
      entity.Property(e => e.Message).IsRequired().HasMaxLength(1000);
      entity.Property(e => e.ActionUrl).HasMaxLength(500);
      entity.Property(e => e.ActionText).HasMaxLength(100);
      entity.Property(e => e.IsRead).HasDefaultValue(false);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.RelatedConnection)
        .WithMany()
        .HasForeignKey(e => e.RelatedConnectionId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.IsRead });
      entity.HasIndex(e => e.CreatedAt);
      entity.HasIndex(e => e.Type);
    });

    // UserOnboardingQuiz entity configuration
    modelBuilder.Entity<UserOnboardingQuiz>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.PrimaryFinancialGoal).HasMaxLength(500); // Increased for JSON arrays
      entity.Property(e => e.BiggestChallenge).HasMaxLength(500); // Increased for JSON arrays
      entity.Property(e => e.SpendingPersonality).HasMaxLength(500); // Increased for JSON arrays
      entity.Property(e => e.SubscriptionAwareness).HasMaxLength(50); // Single value, keep 50
      entity.Property(e => e.SavingsBarriers).HasMaxLength(1000); // JSON array
      entity.Property(e => e.PreferredInsightType).HasMaxLength(500); // Increased for JSON arrays
      entity.Property(e => e.FinancialExperience).HasMaxLength(50); // Single value, keep 50
      entity.Property(e => e.AdditionalNotes).HasMaxLength(2000);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.Version).HasDefaultValue(1);

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.Version });
      entity.HasIndex(e => e.CreatedAt);
    });

    // UserSubscription entity configuration (BuoyAI billing)
    modelBuilder.Entity<UserSubscription>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Tier).IsRequired();
      entity.Property(e => e.BillingCycle).IsRequired();
      entity.Property(e => e.Amount).HasPrecision(18, 2).IsRequired();
      entity.Property(e => e.StartDate).IsRequired();
      entity.Property(e => e.Status).IsRequired();
      entity.Property(e => e.StripeCustomerId).HasMaxLength(255);
      entity.Property(e => e.StripeSubscriptionId).HasMaxLength(255);
      entity.Property(e => e.PromoCode).HasMaxLength(100);
      entity.Property(e => e.Provider).IsRequired().HasDefaultValue(SubscriptionProvider.Stripe);
      entity.Property(e => e.ExternalId).HasMaxLength(255);
      entity.HasIndex(e => new { e.Provider, e.ExternalId });
      entity.Property(e => e.DiscountAmount).HasPrecision(18, 2);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(u => u.UserSubscriptions)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.StripeCustomerId);
      // Unique so two webhooks (checkout.session.completed + customer.subscription.created)
      // can't both insert a row for the same Stripe subscription. Filtered to allow many
      // NULLs (rows without a Stripe subscription id).
      entity.HasIndex(e => e.StripeSubscriptionId)
        .IsUnique()
        .HasFilter("\"StripeSubscriptionId\" IS NOT NULL");
      entity.HasIndex(e => e.Status);
      entity.HasIndex(e => e.CreatedAt);
      
      // Composite index for active subscription queries
      entity.HasIndex(e => new { e.UserId, e.Status }).HasDatabaseName("IX_UserSubscriptions_UserId_Status");
    });

    // SubscriptionPayment entity configuration
    modelBuilder.Entity<SubscriptionPayment>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Amount).HasPrecision(18, 2).IsRequired();
      entity.Property(e => e.Status).IsRequired();
      entity.Property(e => e.StripePaymentIntentId).HasMaxLength(255);
      entity.Property(e => e.StripeInvoiceId).HasMaxLength(255);
      entity.Property(e => e.PaymentDate).IsRequired();
      entity.Property(e => e.FailureReason).HasMaxLength(1000);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.UserSubscription)
        .WithMany(us => us.Payments)
        .HasForeignKey(e => e.UserSubscriptionId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserSubscriptionId);
      entity.HasIndex(e => e.StripePaymentIntentId);
      entity.HasIndex(e => e.StripeInvoiceId);
      entity.HasIndex(e => e.Status);
      entity.HasIndex(e => e.PaymentDate);
      entity.HasIndex(e => e.CreatedAt);
    });

    // Password Reset Token configuration
    modelBuilder.Entity<PasswordResetToken>(entity =>
    {
      entity.ToTable("password_reset_tokens");
      entity.HasKey(e => e.Id);
      
      entity.Property(e => e.Token)
        .IsRequired()
        .HasMaxLength(255);
      
      entity.Property(e => e.IpAddress)
        .HasMaxLength(50);
      
      entity.Property(e => e.UserAgent)
        .HasMaxLength(500);
      
      entity.HasIndex(e => e.Token)
        .IsUnique();
      
      entity.HasIndex(e => e.UserId);
      
      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);
    });

    // User Email Preferences configuration
    modelBuilder.Entity<UserEmailPreferences>(entity =>
    {
      entity.ToTable("user_email_preferences");
      entity.HasKey(e => e.Id);
      
      entity.Property(e => e.UserId)
        .IsRequired();
      
      entity.Property(e => e.CreatedAt)
        .HasDefaultValueSql("CURRENT_TIMESTAMP");
      
      entity.Property(e => e.UpdatedAt)
        .HasDefaultValueSql("CURRENT_TIMESTAMP");
      
      entity.HasIndex(e => e.UserId)
        .IsUnique(); // One preference record per user
      
      entity.HasOne(e => e.User)
        .WithOne()
        .HasForeignKey<UserEmailPreferences>(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.Property(e => e.ProductAnnouncements).HasDefaultValue(true);
    });

    // Admin broadcast (#552) — durable job + per-recipient delivery rows.
    modelBuilder.Entity<Broadcast>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Subject).IsRequired().HasMaxLength(300);
      entity.Property(e => e.BodyMarkdown).IsRequired();
      entity.Property(e => e.BodyHtml).IsRequired();
      entity.Property(e => e.CreatedByUsername).HasMaxLength(256);
      entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(128);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.HasIndex(e => e.IdempotencyKey).IsUnique();
      entity.HasIndex(e => e.Status);
    });

    modelBuilder.Entity<BroadcastRecipient>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Email).IsRequired().HasMaxLength(256);
      entity.HasOne(e => e.Broadcast)
        .WithMany(b => b.Recipients)
        .HasForeignKey(e => e.BroadcastId)
        .OnDelete(DeleteBehavior.Cascade);
      // Drain pending rows per broadcast efficiently; one row per (broadcast,user).
      entity.HasIndex(e => new { e.BroadcastId, e.Status });
      entity.HasIndex(e => new { e.BroadcastId, e.UserId }).IsUnique();
    });

    // TrustedDevice entity configuration
    modelBuilder.Entity<TrustedDevice>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.DeviceFingerprint).IsRequired().HasMaxLength(64);
      entity.Property(e => e.DeviceName).HasMaxLength(200);
      entity.Property(e => e.DeviceType).HasMaxLength(50);
      entity.Property(e => e.IpAddress).HasMaxLength(45);
      entity.Property(e => e.UserAgent).HasMaxLength(500);
      entity.Property(e => e.Location).HasMaxLength(200);
      entity.Property(e => e.LastUsedAt).IsRequired();
      entity.Property(e => e.ExpiresAt).IsRequired();
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.DeviceFingerprint });
      entity.HasIndex(e => e.ExpiresAt);
      entity.HasIndex(e => new { e.UserId, e.IsActive, e.ExpiresAt });
    });

    // LoginAttempt entity configuration
    modelBuilder.Entity<LoginAttempt>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.IpAddress).HasMaxLength(45);
      entity.Property(e => e.UserAgent).HasMaxLength(500);
      entity.Property(e => e.Location).HasMaxLength(200);
      entity.Property(e => e.DeviceFingerprint).HasMaxLength(64);
      entity.Property(e => e.Success).IsRequired();
      entity.Property(e => e.MfaChallenged).IsRequired();
      entity.Property(e => e.MfaSuccess).IsRequired();
      entity.Property(e => e.TrustedDevice).IsRequired();
      entity.Property(e => e.RiskScore).IsRequired();
      entity.Property(e => e.FailureReason).HasMaxLength(200);
      entity.Property(e => e.AttemptedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.AttemptedAt);
      entity.HasIndex(e => new { e.UserId, e.Success });
      entity.HasIndex(e => new { e.UserId, e.AttemptedAt, e.Success });
    });

    // UserMfaPreferences entity configuration
    modelBuilder.Entity<UserMfaPreferences>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.PrimaryMethod).IsRequired();
      entity.Property(e => e.NotifyOnNewDevice).IsRequired();
      entity.Property(e => e.NotifyOnSuspiciousLogin).IsRequired();
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId).IsUnique();
    });

    // UserCategory entity configuration
    modelBuilder.Entity<UserCategory>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Color).IsRequired().HasMaxLength(20).HasDefaultValue("#6B7280");
      entity.Property(e => e.Icon).HasMaxLength(50);
      entity.Property(e => e.Description).HasMaxLength(500);
      entity.Property(e => e.IsIncome).HasDefaultValue(false);
      entity.Property(e => e.SortOrder).HasDefaultValue(0);
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(u => u.UserCategories)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
      entity.HasIndex(e => e.IsActive);
      entity.HasIndex(e => e.SortOrder);
    });

    // Category entity configuration (unified system + user categories)
    modelBuilder.Entity<Category>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Color).IsRequired().HasMaxLength(20).HasDefaultValue("#6B7280");
      entity.Property(e => e.Icon).HasMaxLength(50);
      entity.Property(e => e.Description).HasMaxLength(500);
      entity.Property(e => e.IsIncome).HasDefaultValue(false);
      entity.Property(e => e.IsSystem).HasDefaultValue(false);
      entity.Property(e => e.SortOrder).HasDefaultValue(0);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasOne(e => e.CreatedByUser)
        .WithMany()
        .HasForeignKey(e => e.CreatedByUserId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.FamilyId);
      entity.HasIndex(e => e.IsSystem);
      entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique().HasFilter("\"UserId\" IS NOT NULL");
      entity.HasIndex(e => new { e.FamilyId, e.Name }).IsUnique().HasFilter("\"FamilyId\" IS NOT NULL");
      entity.HasIndex(e => new { e.IsSystem, e.Name }).IsUnique().HasFilter("\"IsSystem\" = true");

      // Seed system categories with explicit static timestamps to avoid PendingModelChangesWarning
      // These timestamps must match exactly what's in the migration file
      var seedDate = new DateTime(2026, 1, 22, 4, 7, 43, 475, DateTimeKind.Utc);
      entity.HasData(
        // Income categories (1-4)
        new Category { Id = 1, Name = "Salary", Color = "#10B981", Icon = "salary", IsIncome = true, IsSystem = true, SortOrder = 1, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 2, Name = "Bonus", Color = "#059669", Icon = "bonus", IsIncome = true, IsSystem = true, SortOrder = 2, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 3, Name = "Investment Income", Color = "#047857", Icon = "investment", IsIncome = true, IsSystem = true, SortOrder = 3, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 4, Name = "Other Income", Color = "#065F46", Icon = "income", IsIncome = true, IsSystem = true, SortOrder = 4, CreatedAt = seedDate, UpdatedAt = seedDate },
        
        // Fixed Expenses (10-14)
        new Category { Id = 10, Name = "Rent/Mortgage", Color = "#EF4444", Icon = "home", IsIncome = false, IsSystem = true, SortOrder = 10, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 11, Name = "Utilities", Color = "#F97316", Icon = "utilities", IsIncome = false, IsSystem = true, SortOrder = 11, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 12, Name = "Insurance", Color = "#F59E0B", Icon = "insurance", IsIncome = false, IsSystem = true, SortOrder = 12, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 13, Name = "Subscriptions", Color = "#EAB308", Icon = "subscriptions", IsIncome = false, IsSystem = true, SortOrder = 13, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 14, Name = "Loan Payments", Color = "#D97706", Icon = "loan", IsIncome = false, IsSystem = true, SortOrder = 14, CreatedAt = seedDate, UpdatedAt = seedDate },
        
        // Variable Expenses (20-29)
        new Category { Id = 20, Name = "Groceries", Color = "#84CC16", Icon = "groceries", IsIncome = false, IsSystem = true, SortOrder = 20, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 21, Name = "Dining Out", Color = "#22C55E", Icon = "dining", IsIncome = false, IsSystem = true, SortOrder = 21, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 22, Name = "Transportation", Color = "#14B8A6", Icon = "transport", IsIncome = false, IsSystem = true, SortOrder = 22, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 23, Name = "Entertainment", Color = "#06B6D4", Icon = "entertainment", IsIncome = false, IsSystem = true, SortOrder = 23, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 24, Name = "Shopping", Color = "#0EA5E9", Icon = "shopping", IsIncome = false, IsSystem = true, SortOrder = 24, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 25, Name = "Healthcare", Color = "#3B82F6", Icon = "health", IsIncome = false, IsSystem = true, SortOrder = 25, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 26, Name = "Travel", Color = "#6366F1", Icon = "travel", IsIncome = false, IsSystem = true, SortOrder = 26, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 27, Name = "Personal Care", Color = "#8B5CF6", Icon = "personal", IsIncome = false, IsSystem = true, SortOrder = 27, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 28, Name = "Education", Color = "#A855F7", Icon = "education", IsIncome = false, IsSystem = true, SortOrder = 28, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 29, Name = "Gifts & Donations", Color = "#D946EF", Icon = "gifts", IsIncome = false, IsSystem = true, SortOrder = 29, CreatedAt = seedDate, UpdatedAt = seedDate },
        
        // Transfers & Payments (30-31)
        new Category { Id = 30, Name = "Credit Card Payment", Color = "#EC4899", Icon = "credit", IsIncome = false, IsSystem = true, SortOrder = 30, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 31, Name = "Transfer", Color = "#F43F5E", Icon = "transfer", IsIncome = false, IsSystem = true, SortOrder = 31, CreatedAt = seedDate, UpdatedAt = seedDate },
        
        // Special (98-99)
        new Category { Id = 98, Name = "Other", Color = "#6B7280", Icon = "other", IsIncome = false, IsSystem = true, SortOrder = 98, CreatedAt = seedDate, UpdatedAt = seedDate },
        new Category { Id = 99, Name = "Uncategorized", Color = "#9CA3AF", Icon = "uncategorized", IsIncome = false, IsSystem = true, SortOrder = 99, CreatedAt = seedDate, UpdatedAt = seedDate }
      );
    });

    // CategorizationRule entity configuration
    modelBuilder.Entity<CategorizationRule>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Source).HasDefaultValue(RuleSource.UserCreated);
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.Priority).HasDefaultValue(0);
      entity.Property(e => e.PlaidMerchantEntityId).HasMaxLength(100);
      entity.Property(e => e.MerchantContains).HasMaxLength(200);
      entity.Property(e => e.MerchantExact).HasMaxLength(200);
      entity.Property(e => e.DescriptionContains).HasMaxLength(200);
      entity.Property(e => e.MinAmount).HasColumnType("decimal(18,2)");
      entity.Property(e => e.MaxAmount).HasColumnType("decimal(18,2)");
      entity.Property(e => e.TimesApplied).HasDefaultValue(0);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);
        
      entity.HasOne(e => e.Family)
        .WithMany()
        .HasForeignKey(e => e.FamilyId)
        .OnDelete(DeleteBehavior.SetNull);

      // Unified category assignment
      entity.HasOne(e => e.AssignCategoryNav)
        .WithMany()
        .HasForeignKey(e => e.AssignCategoryId)
        .OnDelete(DeleteBehavior.SetNull);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.FamilyId);
      entity.HasIndex(e => e.IsActive);
      entity.HasIndex(e => e.Priority);
      entity.HasIndex(e => e.AssignCategoryId);
      entity.HasIndex(e => e.PlaidMerchantEntityId);
      entity.HasIndex(e => new { e.UserId, e.IsActive, e.Priority });
      entity.HasIndex(e => new { e.FamilyId, e.IsActive, e.Priority });
    });

    // AiConversation entity configuration
    modelBuilder.Entity<AiConversation>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.SessionId).IsRequired();
      entity.Property(e => e.Context).IsRequired().HasMaxLength(50);
      entity.Property(e => e.Title).HasMaxLength(200);
      entity.Property(e => e.IsActive).HasDefaultValue(true);
      entity.Property(e => e.StartedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.LastMessageAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.SessionId).IsUnique();
      entity.HasIndex(e => new { e.UserId, e.IsActive });
      entity.HasIndex(e => new { e.UserId, e.LastMessageAt });
    });

    // AiConversationMessage entity configuration
    modelBuilder.Entity<AiConversationMessage>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Role).IsRequired().HasMaxLength(20);
      entity.Property(e => e.Content).IsRequired();
      entity.Property(e => e.Model).HasMaxLength(100);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Conversation)
        .WithMany(c => c.Messages)
        .HasForeignKey(e => e.ConversationId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.ConversationId);
      entity.HasIndex(e => e.CreatedAt);
    });

    // AiToolExecution entity configuration
    modelBuilder.Entity<AiToolExecution>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.SessionId).IsRequired();
      entity.Property(e => e.ToolName).IsRequired().HasMaxLength(100);
      entity.Property(e => e.WasConfirmed).HasDefaultValue(false);
      entity.Property(e => e.ExecutedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.Message)
        .WithMany(m => m.ToolExecutions)
        .HasForeignKey(e => e.MessageId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.MessageId);
      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.SessionId);
      entity.HasIndex(e => e.ToolName);
      entity.HasIndex(e => e.ExecutedAt);
      entity.HasIndex(e => new { e.UserId, e.ExecutedAt });
    });

    // AiActionFeedback entity configuration
    modelBuilder.Entity<AiActionFeedback>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.Comment).HasMaxLength(1000);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.ToolExecution)
        .WithOne(t => t.Feedback)
        .HasForeignKey<AiActionFeedback>(e => e.ToolExecutionId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.ToolExecutionId).IsUnique();
      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.ThumbsUp);
      entity.HasIndex(e => new { e.UserId, e.CreatedAt });
    });

    // UserAchievement entity configuration
    modelBuilder.Entity<UserAchievement>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.AchievementKey).IsRequired().HasMaxLength(50);
      entity.Property(e => e.Title).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Description).HasMaxLength(500);
      entity.Property(e => e.Icon).HasMaxLength(10);
      entity.Property(e => e.Category).HasMaxLength(50);
      entity.Property(e => e.EarnedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.IsNew).HasDefaultValue(true);

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.AchievementKey }).IsUnique();
    });

    // UserStreak entity configuration
    modelBuilder.Entity<WrappedStatCutoff>(entity =>
    {
      entity.HasKey(e => e.MetricKey);
    });

    modelBuilder.Entity<WrappedBonusCardCache>(entity =>
    {
      entity.HasIndex(e => new { e.UserId, e.Year, e.Month }).IsUnique();
    });

    modelBuilder.Entity<UserStreak>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.StreakType).IsRequired().HasMaxLength(50);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.StreakType }).IsUnique();
    });

    // UserChallenge entity configuration
    modelBuilder.Entity<UserChallenge>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.ChallengeType).IsRequired().HasMaxLength(50);
      entity.Property(e => e.Title).IsRequired().HasMaxLength(100);
      entity.Property(e => e.Description).HasMaxLength(500);
      entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("active");
      entity.Property(e => e.TargetValue).HasPrecision(18, 2);
      entity.Property(e => e.CurrentValue).HasPrecision(18, 2);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany()
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.Status });
      entity.HasIndex(e => e.EndDate);
    });

    // UserAiMemory entity configuration
    modelBuilder.Entity<UserAiMemory>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.MemoryType).IsRequired().HasMaxLength(50);
      entity.Property(e => e.MemoryKey).IsRequired().HasMaxLength(100);
      entity.Property(e => e.MemoryValue).IsRequired();
      entity.Property(e => e.DisplayText).IsRequired().HasMaxLength(500);
      entity.Property(e => e.Confidence).HasPrecision(3, 2).HasDefaultValue(1.0m);
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
      entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(u => u.AiMemories)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => new { e.UserId, e.MemoryType, e.MemoryKey }).IsUnique();
      entity.HasIndex(e => new { e.UserId, e.IsActive });
      entity.HasIndex(e => e.MemoryType);
    });

    // AiToolAuditLog entity configuration
    modelBuilder.Entity<AiToolAuditLog>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.ToolName).IsRequired().HasMaxLength(100);
      entity.Property(e => e.ArgumentsJson).IsRequired();
      entity.Property(e => e.IpAddress).HasMaxLength(45);
      entity.Property(e => e.UserAgent).HasMaxLength(500);
      entity.Property(e => e.ExecutedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(u => u.AiToolAuditLogs)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.ToolName);
      entity.HasIndex(e => e.ExecutedAt);
      entity.HasIndex(e => new { e.UserId, e.ExecutedAt });
      entity.HasIndex(e => e.SessionId);
    });

    // PendingToolConfirmation entity configuration
    modelBuilder.Entity<PendingToolConfirmation>(entity =>
    {
      entity.HasKey(e => e.Id);
      entity.Property(e => e.ConfirmationToken).IsRequired();
      entity.Property(e => e.ToolName).IsRequired().HasMaxLength(100);
      entity.Property(e => e.ArgumentsJson).IsRequired();
      entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

      entity.HasOne(e => e.User)
        .WithMany(u => u.PendingToolConfirmations)
        .HasForeignKey(e => e.UserId)
        .OnDelete(DeleteBehavior.Cascade);

      entity.HasIndex(e => e.ConfirmationToken).IsUnique();
      entity.HasIndex(e => e.UserId);
      entity.HasIndex(e => e.ExpiresAt);
      entity.HasIndex(e => new { e.UserId, e.IsUsed, e.ExpiresAt });
    });
  }
}
