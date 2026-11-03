using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuoyAI.Core.DTOs.Export;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Full data export. EF only (no raw SQL), AsNoTracking, and every row passes through an
/// allow-list DTO so no secret column can leak by accident.
/// Scope: rows the caller owns, plus family rows they can already see in the app (the caller
/// has an active FamilyMember row for the same FamilyId). Family rows owned by someone else are
/// marked Shared = true and carry only the owner's user id, never another member's email or name.
/// Memory: each table is read in keyset pages (Id &gt; last, page size 1000) and written straight to
/// the destination stream, so memory use is one page plus the small id sets in <see cref="ExportScope"/>.
/// </summary>
public class UserDataExportService : IUserDataExportService
{
    public const int SchemaVersion = 1;
    private const int DefaultPageSize = 1000;

    private readonly BuoyAIDbContext _context;
    private readonly int _pageSize;

    public UserDataExportService(BuoyAIDbContext context, int pageSize = DefaultPageSize)
    {
        _context = context;
        _pageSize = pageSize;
    }

    // ---- Coverage contract (checked by a reflection test) ----

    /// <summary>Entity types that appear in the export (directly or nested under a parent).</summary>
    public static readonly IReadOnlySet<Type> ExportedEntityTypes = new HashSet<Type>
    {
        typeof(User), typeof(Family), typeof(FamilyMember), typeof(Account), typeof(Transaction),
        typeof(Budget), typeof(BudgetCategory), typeof(BudgetTransfer), typeof(BudgetShare),
        typeof(BudgetPeriodSnapshot), typeof(BudgetPeriodCategorySnapshot), typeof(Category),
        typeof(CategoryGroup), typeof(UserCategory), typeof(CategorizationRule), typeof(SavingsGoal),
        typeof(SavingsAllocation), typeof(Subscription), typeof(SubscriptionTransaction),
        typeof(SuggestedSubscription), typeof(DismissedSubscriptionPattern), typeof(AiConversation),
        typeof(AiConversationMessage), typeof(UserAiMemory), typeof(AiToolExecution),
        typeof(AiActionFeedback), typeof(AIInsight), typeof(UserVisualization), typeof(Notification), typeof(ReceiptScan),
        typeof(UserAchievement), typeof(UserStreak), typeof(UserChallenge), typeof(UserConsent),
        typeof(UserOnboardingQuiz), typeof(PlaidConnection), typeof(Referral), typeof(UserSubscription),
        typeof(SubscriptionPayment), typeof(UserEntitlement), typeof(UserEmailPreferences), typeof(UserPushPreferences),
    };

    /// <summary>
    /// Entity types that carry a user reference but are deliberately NOT exported, with the reason.
    /// Adding a table with a UserId without listing it here or in <see cref="ExportedEntityTypes"/>
    /// fails the coverage test.
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string> ExcludedEntityReasons = new Dictionary<Type, string>
    {
        [typeof(UserMfaPreferences)] = "covered by user.mfa: enabled flag and method only, no secrets",
        [typeof(RefreshToken)] = "credential: session token hash",
        [typeof(PasswordResetToken)] = "credential: reset token",
        [typeof(TwoFactorCode)] = "credential: one-time login code",
        [typeof(UserPasskey)] = "credential: passkey public key and id",
        [typeof(PasskeyChallenge)] = "credential: live sign-in challenge",
        [typeof(TrustedDevice)] = "credential: device fingerprint",
        [typeof(LoginAttempt)] = "security telemetry (IP addresses)",
        [typeof(UserDeviceToken)] = "credential: push token",
        [typeof(RegistrationInvite)] = "credential: single-use sign-up code hash (self-host admin tooling)",
        [typeof(QueuedPushNotification)] = "operational delivery queue",
        [typeof(OAuthRefreshToken)] = "credential: connector token",
        [typeof(ConnectPersonalToken)] = "credential: personal access token hash",
        [typeof(OAuthAuthorizationRequest)] = "credential: live authorization request",
        [typeof(UserExternalLogin)] = "credential: single sign-on provider account id",
        [typeof(OidcLoginRequest)] = "credential: live single sign-on request",
        [typeof(SecurityAuditLog)] = "security operations log (IP addresses)",
        [typeof(ErrorLog)] = "operational error log",
        [typeof(DataRetentionAuditLog)] = "operational retention audit",
        [typeof(AIRefreshUsage)] = "operational rate counter",
        [typeof(AIUsageRecord)] = "operational AI usage metering",
        [typeof(AICategorizationQueueItem)] = "operational work queue",
        [typeof(AiToolAuditLog)] = "security audit of AI tool calls",
        [typeof(PendingToolConfirmation)] = "credential: live confirmation token",
        [typeof(WrappedStatCutoff)] = "derived cache",
        [typeof(WrappedBonusCardCache)] = "derived cache",
        [typeof(TrialHistory)] = "anti-abuse bookkeeping",
        [typeof(FamilyInvitation)] = "other people's data: invitee emails and invite tokens",
        [typeof(BroadcastRecipient)] = "operational marketing delivery log",
        [typeof(ProcessedStripeEvent)] = "operational webhook dedupe",
        [typeof(ProcessedRevenueCatEvent)] = "operational webhook dedupe",
        [typeof(SyncIdempotencyKey)] = "operational request dedupe",
        [typeof(CrowdsourcedSubscriptionPattern)] = "cross-user aggregate, not the user's data",
        [typeof(Broadcast)] = "admin-authored marketing, not the user's data",
        [typeof(MarketingLead)] = "marketing leads, not account data",
        [typeof(OAuthClient)] = "connector registry, not the user's data",
    };

    /// <summary>Category names written into EXCLUDED.md and the JSON "excluded" list. No values.</summary>
    public static readonly IReadOnlyList<string> ExcludedCategories = new[]
    {
        "Passwords, password reset tokens and recovery codes",
        "Sign-in sessions, refresh tokens and one-time login codes",
        "Two-factor secrets, passkeys and trusted devices",
        "Connector (OAuth) tokens and push notification tokens",
        "Bank connection access tokens, item ids and reconnect links",
        "Stripe and app store customer, subscription and payment ids",
        "Security, login and error logs (they contain IP addresses)",
        "Live AI action confirmation tokens",
        "Other people's data: family member emails, names and invitations",
        "Cross-user aggregates and internal caches, queues and usage meters",
    };

    // ---- Scope ----

    public async Task<ExportScope?> ResolveScopeAsync(int userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null)
        {
            return null;
        }

        // Family access = user.FamilyId set AND an active membership row for that family.
        var membership = user.FamilyId == null
            ? null
            : await _context.FamilyMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.FamilyId == user.FamilyId && m.UserId == userId && m.IsActive, cancellationToken);
        var hasFamily = membership != null;
        var familyId = membership?.FamilyId ?? 0;
        var readOnly = membership?.Role == FamilyMemberRole.ReadOnly;

        var accountEntities = await _context.Accounts.AsNoTracking()
            .Where(a => a.UserId == userId || (hasFamily && a.FamilyId == familyId && a.IsActive))
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
        var accounts = accountEntities.Select(a => new ExportAccountDto(
            a.Id, a.UserId, a.UserId != userId, a.FamilyId, a.AssociatedUserId, a.Name, a.OfficialName, a.Nickname, a.Mask,
            a.Type.ToString(), a.Subtype, a.CurrentBalance, a.AvailableBalance, a.Currency, a.IsActive,
            a.CreatedAt, a.UpdatedAt)).ToList();
        var accountOwners = accountEntities.ToDictionary(a => a.Id, a => a.UserId);
        var accountIds = accountEntities.Select(a => a.Id).ToList();

        var budgetIds = await _context.Budgets.AsNoTracking()
            .Where(b => b.UserId == userId || (hasFamily && b.FamilyId == familyId))
            .Select(b => b.Id).ToListAsync(cancellationToken);
        var goalIds = await _context.SavingsGoals.AsNoTracking()
            .Where(g => g.UserId == userId || (hasFamily && g.FamilyId == familyId))
            .Select(g => g.Id).ToListAsync(cancellationToken);
        var subscriptionIds = await _context.Subscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => s.Id).ToListAsync(cancellationToken);

        // A ReadOnly member must not get a hidden transaction, nor the split children of one.
        var hiddenIds = new HashSet<int>();
        if (readOnly)
        {
            var sharedAccountIds = accountEntities.Where(a => a.UserId != userId).Select(a => a.Id).ToList();
            hiddenIds = (await _context.Transactions.AsNoTracking()
                .Where(t => sharedAccountIds.Contains(t.AccountId) && t.HiddenFromReadOnly)
                .Select(t => t.Id).ToListAsync(cancellationToken)).ToHashSet();
        }

        var emailPrefs = await _context.UserEmailPreferences.AsNoTracking()
            .Where(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        var pushPrefs = await _context.UserPushPreferences.AsNoTracking()
            .Where(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        var preferences = new ExportPreferencesDto(
            emailPrefs == null ? null : new ExportEmailPreferencesDto(
                emailPrefs.PlaidConnectionIssues, emailPrefs.PlaidReconnectionSuccess, emailPrefs.SubscriptionChanges,
                emailPrefs.TrialExpirationWarnings, emailPrefs.BudgetAlerts, emailPrefs.SavingsGoalMilestones,
                emailPrefs.LargeTransactionAlerts, emailPrefs.MonthlySummary, emailPrefs.FeatureAnnouncements,
                emailPrefs.TipsAndBestPractices, emailPrefs.ProductUpdates, emailPrefs.PromotionalOffers,
                emailPrefs.ReferralInvites, emailPrefs.ProductAnnouncements, emailPrefs.BadgeEarned,
                emailPrefs.MilestoneReached, emailPrefs.StreakNotifications),
            pushPrefs == null ? null : new ExportPushPreferencesDto(
                pushPrefs.PushEnabled, pushPrefs.ConnectionIssues, pushPrefs.BudgetAlerts, pushPrefs.BillReminders,
                pushPrefs.SubscriptionDetection, pushPrefs.TransactionReview, pushPrefs.QuietHoursEnabled,
                pushPrefs.QuietHoursStartMinutes, pushPrefs.QuietHoursEndMinutes, pushPrefs.TimeZoneId),
            new ExportMfaDto(user.HasMfaEnabled || user.HasPasskeyRegistered, user.MfaType.ToString(), user.HasPasskeyRegistered));

        // Family: name and the member list as ids and roles only (no other member's email or name).
        ExportFamilyDto? family = null;
        if (hasFamily)
        {
            var familyEntity = await _context.Families.AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == familyId, cancellationToken);
            if (familyEntity != null)
            {
                var members = (await _context.FamilyMembers.AsNoTracking()
                        .Where(m => m.FamilyId == familyId && m.IsActive)
                        .OrderBy(m => m.Id)
                        .ToListAsync(cancellationToken))
                    .Select(m => new ExportFamilyMemberDto(m.UserId, m.Role.ToString(), m.JoinedAt, m.IsActive)).ToList();
                family = new ExportFamilyDto(familyEntity.Id, familyEntity.Name, familyEntity.CreatedAt, members);
            }
        }

        var userDto = new ExportUserDto(
            user.Id, user.Username, user.Email, user.FirstName, user.LastName, user.Currency,
            user.SignupCountry, user.PreferredLanguage, user.WeekStartDay, user.CreatedAt, user.UpdatedAt,
            user.IsEmailVerified, user.HasMfaEnabled, user.SubscriptionTier?.ToString(),
            user.SubscriptionStartDate, user.SubscriptionEndDate, user.TrialStartDate, user.TrialEndDate,
            user.FamilyId, user.AiInteractionMode.ToString(), user.AiAutonomyLevel.ToString(),
            user.AiMemoryEnabled, user.AiConsentGrantedAt, user.AiConsentWithdrawnAt, user.AiConsentVersion,
            user.ExcludePendingFromBudget, user.ReattributeOnAccountAssociationChange);

        return new ExportScope
        {
            UserId = userId,
            HasMfa = user.HasMfaEnabled || user.HasPasskeyRegistered,
            HasFamily = hasFamily,
            FamilyId = familyId,
            ReadOnly = readOnly,
            User = userDto,
            Family = family,
            Preferences = preferences,
            Accounts = accounts,
            AccountOwners = accountOwners,
            BudgetIds = budgetIds,
            GoalIds = goalIds,
            SubscriptionIds = subscriptionIds,
            HiddenTransactionIds = hiddenIds,
        };
    }

    public async Task RecordExportAsync(int userId, string format, CancellationToken cancellationToken = default)
    {
        _context.SecurityAuditLogs.Add(new SecurityAuditLog
        {
            UserId = userId,
            EventType = "DATA_EXPORT",
            Description = $"User exported their data ({format})",
            AdditionalData = JsonSerializer.Serialize(new { format }),
            CreatedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    // ---- Tables ----

    private sealed record ExportTable(
        string JsonName, string CsvName, Type RowType, Type EntityType,
        Func<CancellationToken, IAsyncEnumerable<object>> Rows);

    private ExportTable Table<TEntity, TDto>(
        string jsonName, string csvName, IQueryable<TEntity> query, Func<TEntity, int> idOf,
        Func<TEntity, TDto?> map)
        where TEntity : class
        where TDto : class =>
        new(jsonName, csvName, typeof(TDto), typeof(TEntity), ct => Paged(query, idOf, map, ct));

    /// <summary>Keyset paging: Id greater than the last seen Id, ascending, one page at a time.</summary>
    private async IAsyncEnumerable<object> Paged<TEntity, TDto>(
        IQueryable<TEntity> query, Func<TEntity, int> idOf, Func<TEntity, TDto?> map,
        [EnumeratorCancellation] CancellationToken ct)
        where TEntity : class
        where TDto : class
    {
        var last = 0;
        while (true)
        {
            var after = last;
            var page = await query
                .Where(e => EF.Property<int>(e, "Id") > after)
                .OrderBy(e => EF.Property<int>(e, "Id"))
                .Take(_pageSize)
                .ToListAsync(ct);
            foreach (var entity in page)
            {
                var dto = map(entity);
                if (dto != null)
                {
                    yield return dto;
                }
            }
            if (page.Count < _pageSize)
            {
                yield break;
            }
            last = idOf(page[^1]);
        }
    }

    private List<ExportTable> BuildTables(ExportScope s)
    {
        var userId = s.UserId;
        var hasFamily = s.HasFamily;
        var familyId = s.FamilyId;
        var readOnly = s.ReadOnly;
        var accountIds = s.AccountOwners.Keys.ToList();
        var budgetIds = s.BudgetIds.ToList();
        var goalIds = s.GoalIds.ToList();
        var subscriptionIds = s.SubscriptionIds.ToList();

        return new List<ExportTable>
        {
            new("accounts", "accounts", typeof(ExportAccountDto), typeof(Account), _ => ToAsync(s.Accounts.Cast<object>())),

            Table("transactions", "transactions",
                _context.Transactions.AsNoTracking().Where(t => accountIds.Contains(t.AccountId)),
                t => t.Id,
                t =>
                {
                    var shared = s.AccountOwners[t.AccountId] != userId;
                    if (shared && readOnly && (t.HiddenFromReadOnly
                        || (t.ParentTransactionId != null && s.HiddenTransactionIds.Contains(t.ParentTransactionId.Value))))
                    {
                        return null;
                    }
                    return new ExportTransactionDto(
                        t.Id, t.AccountId, shared, DateOnly.FromDateTime(t.Date),
                        t.AuthorizedDate == null ? null : DateOnly.FromDateTime(t.AuthorizedDate.Value),
                        t.Amount, t.Type.ToString(), t.Description, t.MerchantName, t.Nickname, t.CategoryId,
                        t.IsUserCategorized, t.CategorySource, t.PlaidCategoryPrimary, t.PlaidCategoryDetailed,
                        t.PlaidPaymentChannel, t.LocationCity, t.LocationRegion, t.OriginalDescription,
                        t.IsoCurrencyCode, t.IsPending, t.BudgetId, t.SavingsGoalId, t.UserNotes, t.IsSplit,
                        t.ParentTransactionId, t.CreatedAt, t.UpdatedAt,
                        t.ClaimedByUserId,
                        // The private allowance sub-category is only visible to the member who claimed it.
                        t.ClaimedByUserId == userId ? t.PersonalUserCategoryId : null,
                        t.AISuggestedCategoryId, t.AIConfidence, t.AICategorizationDate, t.NeedsReview,
                        t.SkipUntil, t.SemanticTags, t.CoveringTransferTransactionId, t.TransferReviewedAt);
                }),

            Table("budgets", "budgets",
                _context.Budgets.AsNoTracking().Where(b => b.UserId == userId || (hasFamily && b.FamilyId == familyId)),
                b => b.Id,
                b => new ExportBudgetDto(
                    b.Id, b.UserId, b.UserId != userId, b.FamilyId, b.Name, b.Period.ToString(),
                    b.CurrentPeriodStart, b.CurrentPeriodEnd, b.AutoReset, b.TotalIncome, b.TotalExpenses,
                    b.IsActive, b.ParentBudgetId, b.AllocatedAmount, b.Type.ToString(), b.IsPrimary,
                    b.RolloverMode.ToString(), b.DefaultSavingsGoalId,
                    b.CreatedAt, b.UpdatedAt)),

            Table("budgetCategories", "budget_categories",
                _context.BudgetCategories.AsNoTracking().Where(c => budgetIds.Contains(c.BudgetId)),
                c => c.Id,
                c => new ExportBudgetCategoryDto(
                    c.Id, c.BudgetId, c.CategoryId, c.GroupId, c.SortOrder, c.IsIncome, c.AllocatedAmount,
                    c.DefaultAllocatedAmount, c.AllowRollover, c.RolloverBalance, c.IsCountUpEnabled,
                    c.DailyAllowance, c.EarnedToDate, c.SavingsGoalId, c.IsAllowance, c.LinkedBudgetId,
                    c.AssignedUserId, c.MaxRolloverMultiplier, c.RolloverOverflowSavingsGoalId,
                    c.LastAllowanceCalculation, c.CountUpRolloverMode?.ToString(), c.CreatedAt, c.UpdatedAt)),

            Table("budgetTransfers", "budget_transfers",
                _context.BudgetTransfers.AsNoTracking().Where(t => budgetIds.Contains(t.BudgetId)),
                t => t.Id,
                t => new ExportBudgetTransferDto(
                    t.Id, t.BudgetId, t.CreatedByUserId, t.SourceCategoryId, t.SourceSavingsGoalId,
                    t.DestinationCategoryId, t.DestinationSavingsGoalId, t.Amount, t.Note,
                    t.Reason.ToString(), t.CreatedAt)),

            // Budget owner sees every share on the budget; a shared user sees only their own row.
            Table("budgetShares", "budget_shares",
                _context.BudgetShares.AsNoTracking().Where(b => budgetIds.Contains(b.BudgetId)
                    && (b.SharedWithUserId == userId
                        || _context.Budgets.Any(x => x.Id == b.BudgetId && x.UserId == userId))),
                b => b.Id,
                b => new ExportBudgetShareDto(
                    b.Id, b.BudgetId, b.SharedWithUserId, b.CanEdit, b.CanViewTransactions, b.CreatedAt)),

            Table("budgetPeriodSnapshots", "budget_period_snapshots",
                _context.BudgetPeriodSnapshots.AsNoTracking().Where(p => budgetIds.Contains(p.BudgetId)),
                p => p.Id,
                p => new ExportBudgetPeriodSnapshotDto(
                    p.Id, p.BudgetId, DateOnly.FromDateTime(p.PeriodStart), DateOnly.FromDateTime(p.PeriodEnd),
                    p.TotalAllocated, p.TotalSpent, p.TotalIncome, p.TotalRolloverIn, p.TotalRolloverOut,
                    p.CreatedAt)),

            Table("budgetPeriodCategorySnapshots", "budget_period_category_snapshots",
                _context.BudgetPeriodCategorySnapshots.AsNoTracking().Where(c =>
                    _context.BudgetPeriodSnapshots.Any(p => p.Id == c.BudgetPeriodSnapshotId && budgetIds.Contains(p.BudgetId))),
                c => c.Id,
                c => new ExportBudgetPeriodCategorySnapshotDto(
                    c.Id, c.BudgetPeriodSnapshotId, c.BudgetCategoryId, c.CategoryId, c.CategoryName,
                    c.AllocatedAmount, c.SpentAmount, c.RolloverIn, c.RolloverOut, c.AllowRollover, c.IsIncome)),

            // System categories are shared reference data that transactions point at, so they are
            // included to keep the export self-contained.
            Table("categories", "categories",
                _context.Categories.AsNoTracking().Where(c => c.IsSystem || c.UserId == userId
                    || c.CreatedByUserId == userId || (hasFamily && c.FamilyId == familyId)
                    // Same guarded branch as CategoryRepository.GetAllForUserAsync: a family member's
                    // personal category that a family budget references, so BudgetCategory.CategoryId resolves.
                    || (hasFamily && c.UserId != null && c.UserId != userId
                        && _context.Users.Any(u => u.Id == c.UserId && u.FamilyId == familyId)
                        && _context.BudgetCategories.Any(bc => bc.CategoryId == c.Id && bc.Budget.FamilyId == familyId))),
                c => c.Id,
                c =>
                {
                    var owner = c.UserId ?? c.CreatedByUserId;
                    return new ExportCategoryDto(
                        c.Id, c.Name, c.Color, c.Icon, c.IsIncome, c.IsSystem, c.SortOrder, c.Description,
                        owner, !c.IsSystem && owner != userId, c.FamilyId);
                }),

            // System groups (no user, no family) are included so BudgetCategory.GroupId resolves.
            Table("categoryGroups", "category_groups",
                _context.CategoryGroups.AsNoTracking().Where(g => g.UserId == userId
                    || (hasFamily && g.FamilyId == familyId) || (g.UserId == null && g.FamilyId == null)),
                g => g.Id,
                g => new ExportCategoryGroupDto(
                    g.Id, g.Name, g.Icon, g.SortOrder, g.UserId, g.UserId != null && g.UserId != userId, g.FamilyId)),

            Table("userCategories", "user_categories",
                _context.UserCategories.AsNoTracking().Where(c => c.UserId == userId),
                c => c.Id,
                c => new ExportUserCategoryDto(
                    c.Id, c.Name, c.Color, c.Icon, c.IsIncome, c.IsPersonal, c.Description, c.SortOrder,
                    c.IsActive, c.CreatedAt, c.UpdatedAt)),

            Table("categorizationRules", "categorization_rules",
                _context.CategorizationRules.AsNoTracking().Where(r => r.UserId == userId || (hasFamily && r.FamilyId == familyId)),
                r => r.Id,
                r => new ExportCategorizationRuleDto(
                    r.Id, r.UserId, r.UserId != userId, r.FamilyId, r.Name, r.Source.ToString(), r.IsActive,
                    r.Priority, r.MerchantContains, r.MerchantExact, r.DescriptionContains, r.MinAmount,
                    r.MaxAmount, r.AssignCategoryId, r.TimesApplied, r.LastAppliedAt, r.CreatedAt)),

            Table("savingsGoals", "savings_goals",
                _context.SavingsGoals.AsNoTracking().Where(g => g.UserId == userId || (hasFamily && g.FamilyId == familyId)),
                g => g.Id,
                g => new ExportSavingsGoalDto(
                    g.Id, g.UserId, g.UserId != userId, g.FamilyId, g.Type.ToString(), g.Name, g.Description,
                    g.TargetAmount, g.CurrentAmount,
                    g.TargetDate == null ? null : DateOnly.FromDateTime(g.TargetDate.Value),
                    g.IsActive, g.IsCompleted, g.AccountId, g.CreatedAt, g.UpdatedAt)),

            Table("savingsAllocations", "savings_allocations",
                _context.SavingsAllocations.AsNoTracking().Where(a => goalIds.Contains(a.SavingsGoalId)),
                a => a.Id,
                a => new ExportSavingsAllocationDto(
                    a.Id, a.SavingsGoalId, a.BudgetId, a.AllocatedAmount, a.LeftoverBudget,
                    DateOnly.FromDateTime(a.AllocationDate), a.Source, a.CreatedAt)),

            Table("subscriptions", "subscriptions",
                _context.Subscriptions.AsNoTracking().Where(x => subscriptionIds.Contains(x.Id)),
                x => x.Id,
                x => new ExportSubscriptionDto(
                    x.Id, x.UserId, x.UserId != userId, x.FamilyId, x.Name, x.Description, x.Amount,
                    x.Frequency.ToString(), DateOnly.FromDateTime(x.NextDueDate),
                    x.LastBilledDate == null ? null : DateOnly.FromDateTime(x.LastBilledDate.Value),
                    x.Status.ToString(), x.Category.ToString(), x.IsDetected, x.MerchantName, x.CreatedAt, x.UpdatedAt)),

            Table("subscriptionTransactions", "subscription_transactions",
                _context.SubscriptionTransactions.AsNoTracking().Where(x => subscriptionIds.Contains(x.SubscriptionId)),
                x => x.Id,
                // A link can name a transaction a ReadOnly member is not allowed to see.
                x => s.HiddenTransactionIds.Contains(x.TransactionId)
                    ? null
                    : new ExportSubscriptionTransactionDto(x.Id, x.SubscriptionId, x.TransactionId, x.CreatedAt)),

            Table("suggestedSubscriptions", "suggested_subscriptions",
                _context.SuggestedSubscriptions.AsNoTracking().Where(x => x.UserId == userId),
                x => x.Id,
                x => new ExportSuggestedSubscriptionDto(
                        x.Id, x.UserId, x.UserId != userId, x.FamilyId, x.NormalizedMerchantName,
                        x.DisplayMerchantName, x.Amount, x.Frequency.ToString(), x.Confidence, x.Status.ToString(),
                        x.SnoozeUntil, x.AISuggestedCategory?.ToString(), x.AIInsight, x.MatchedTransactionCount,
                        x.LastTransactionDate == null ? null : DateOnly.FromDateTime(x.LastTransactionDate.Value),
                        x.PredictedNextDate == null ? null : DateOnly.FromDateTime(x.PredictedNextDate.Value),
                        x.DetectedAt, x.ActionTakenAt, x.DismissReason, x.ConfirmedSubscriptionId, x.CreatedAt)),

            Table("dismissedSubscriptionPatterns", "dismissed_subscription_patterns",
                _context.DismissedSubscriptionPatterns.AsNoTracking().Where(x => x.UserId == userId),
                x => x.Id,
                x => new ExportDismissedSubscriptionPatternDto(
                    x.Id, x.MerchantName, x.AmountMin, x.AmountMax, x.DismissedAt, x.DismissReason, x.IsPermanent)),

            // Chat history and everything the AI did is private to the user, even inside a family.
            Table("aiMemories", "ai_memories",
                _context.UserAiMemories.AsNoTracking().Where(m => m.UserId == userId),
                m => m.Id,
                m => new ExportAiMemoryDto(
                    m.Id, m.MemoryType, m.MemoryKey, m.MemoryValue, m.DisplayText, m.Confidence, m.LearnedAt,
                    m.IsActive, m.CreatedAt)),

            Table("aiToolExecutions", "ai_tool_executions",
                _context.AiToolExecutions.AsNoTracking().Where(x => x.UserId == userId),
                x => x.Id,
                // Free-form tool arguments and results can quote family data that a ReadOnly member
                // is hidden from, so those two fields are blanked for ReadOnly members.
                x => new ExportAiToolExecutionDto(
                    x.Id, x.MessageId, x.SessionId, x.ToolName, readOnly ? null : x.Arguments,
                    readOnly ? null : x.Result, x.WasConfirmed, x.Success, x.DurationMs, x.ExecutedAt)),

            Table("aiActionFeedback", "ai_action_feedback",
                _context.AiActionFeedback.AsNoTracking().Where(x => x.UserId == userId),
                x => x.Id,
                x => new ExportAiActionFeedbackDto(x.Id, x.ToolExecutionId, x.ThumbsUp, x.Comment, x.CreatedAt, x.UpdatedAt)),

            Table("insights", "insights",
                _context.AIInsights.AsNoTracking().Where(i => i.UserId == userId),
                i => i.Id,
                i => new ExportInsightDto(
                    i.Id, i.UserId, i.UserId != userId, i.FamilyId, i.Context, i.Type, i.Title, i.Message,
                    i.ActionSuggestion, i.Confidence, i.Priority, i.IsDismissed, i.IsArchived, i.GeneratedAt)),

            Table("visualizations", "visualizations",
                _context.UserVisualizations.AsNoTracking().Where(v => v.UserId == userId),
                v => v.Id,
                v => new ExportVisualizationDto(
                    v.Id, v.UserId, v.UserId != userId, v.FamilyId, v.ChartType, v.Title, v.Message,
                    v.MotivationalMessage, v.ChartData, v.ChartConfig, v.Category, v.Priority, v.Confidence,
                    v.IsStarred, v.IsDismissed, v.GeneratedAt, v.CreatedAt)),

            Table("receiptScans", "receipt_scans",
                _context.ReceiptScans.AsNoTracking().Where(r => r.UserId == userId),
                r => r.Id,
                r => new ExportReceiptScanDto(
                    r.Id, r.Status, r.Retailer, r.OrderNumber, r.OrderDate, r.Total, r.ParsedJson, r.ProposalJson,
                    r.TransactionId, r.Error, r.ReceivedAt, r.MatchedAt, r.AppliedAt)),

            Table("notifications", "notifications",
                _context.Notifications.AsNoTracking().Where(n => n.UserId == userId),
                n => n.Id,
                n => new ExportNotificationDto(n.Id, n.Type, n.Title, n.Message, n.IsRead, n.ReadAt, n.CreatedAt)),

            Table("achievements", "achievements",
                _context.UserAchievements.AsNoTracking().Where(a => a.UserId == userId),
                a => a.Id,
                a => new ExportAchievementDto(
                    a.Id, a.AchievementKey, a.Title, a.Description, a.Category, a.Points, a.EarnedAt,
                    a.TimesEarned, a.RevokedAt)),

            Table("streaks", "streaks",
                _context.UserStreaks.AsNoTracking().Where(x => x.UserId == userId),
                x => x.Id,
                x => new ExportStreakDto(
                    x.Id, x.StreakType, x.CurrentCount, x.BestCount, x.LastIncrementDate, x.StreakStartDate)),

            Table("challenges", "challenges",
                _context.UserChallenges.AsNoTracking().Where(c => c.UserId == userId),
                c => c.Id,
                c => new ExportChallengeDto(
                    c.Id, c.ChallengeType, c.Title, c.Description, c.TargetValue, c.CurrentValue,
                    c.RewardPoints, c.StartDate, c.EndDate, c.Status, c.CompletedAt)),

            // Consent records: type, version, decision and time only (no IP address or user agent).
            Table("consents", "consents",
                _context.UserConsents.AsNoTracking().Where(c => c.UserId == userId),
                c => c.Id,
                c => new ExportConsentDto(c.Id, c.ConsentType, c.ConsentVersion, c.HasConsented, c.ConsentedAt)),

            Table("onboardingQuiz", "onboarding_quiz",
                _context.UserOnboardingQuizzes.AsNoTracking().Where(q => q.UserId == userId),
                q => q.Id,
                q => new ExportOnboardingQuizDto(
                    q.Id, q.PrimaryFinancialGoal, q.BiggestChallenge, q.SpendingPersonality,
                    q.SubscriptionAwareness, q.SavingsBarriers, q.PreferredInsightType, q.FinancialExperience,
                    q.HasUsedBudgetingApps, q.AdditionalNotes, q.Version, q.CreatedAt, q.UpdatedAt)),

            // Bank connection metadata for the connection owner only. Token, item id and
            // reconnect link have no DTO field.
            Table("plaidConnections", "plaid_connections",
                _context.PlaidConnections.AsNoTracking().Where(c => c.UserId == userId),
                c => c.Id,
                c => new ExportPlaidConnectionDto(
                    c.Id, c.UserId, c.FamilyId, c.Provider.ToString(), c.InstitutionId, c.InstitutionName,
                    c.IsActive, c.Status.ToString(), c.LastSyncAt, c.CreatedAt, c.LastTransactionSyncAt,
                    c.DisconnectDate, c.RequiresUserAction)),

            // Referrals the caller made. The referred person is not identified.
            Table("referrals", "referrals",
                _context.Referrals.AsNoTracking().Where(r => r.ReferrerUserId == userId),
                r => r.Id,
                r => new ExportReferralDto(r.Id, r.ReferralCode, r.RewardStatus, r.CreatedAt)),

            // BuoyAI plan and payment history. Stripe and store ids have no DTO field.
            Table("userSubscriptions", "user_subscriptions",
                _context.UserSubscriptions.AsNoTracking().Where(x => x.UserId == userId),
                x => x.Id,
                x => new ExportUserSubscriptionDto(
                    x.Id, x.Tier.ToString(), x.BillingCycle.ToString(), x.Amount, x.StartDate, x.EndDate,
                    x.NextBillingDate, x.Status.ToString(), x.PromoCode, x.Provider.ToString(),
                    x.DiscountAmount, x.CreatedAt)),

            Table("userEntitlements", "user_entitlements",
                _context.UserEntitlements.AsNoTracking().Where(x => x.UserId == userId),
                x => x.Id,
                x => new ExportUserEntitlementDto(
                    x.Id, x.Entitlement, x.Quantity, x.Provider.ToString(), x.Status, x.PaidThrough, x.EndsAt,
                    x.Founding, x.CreatedAt)),

            Table("subscriptionPayments", "subscription_payments",
                _context.SubscriptionPayments.AsNoTracking().Where(p =>
                    _context.UserSubscriptions.Any(u => u.Id == p.UserSubscriptionId && u.UserId == userId)),
                p => p.Id,
                p => new ExportSubscriptionPaymentDto(
                    p.Id, p.UserSubscriptionId, p.Amount, p.Status.ToString(), p.PaymentDate, p.FailureDate,
                    p.FailureReason, p.CreatedAt)),
        };
    }

    /// <summary>
    /// Entity types behind the tables this service writes, plus the ones it writes from the scope
    /// (user, family, preferences). Lets a test tie <see cref="ExportedEntityTypes"/> to the real
    /// export instead of trusting a hand-kept list.
    /// </summary>
    public IReadOnlySet<Type> WrittenEntityTypes(ExportScope scope)
    {
        var types = BuildTables(scope).Select(t => t.EntityType).ToHashSet();
        types.Add(ConversationsTable(scope).EntityType);
        types.Add(MessagesTable(scope).EntityType);
        types.UnionWith(new[]
        {
            typeof(User), typeof(Family), typeof(FamilyMember), typeof(UserEmailPreferences),
            typeof(UserPushPreferences),
        });
        return types;
    }

    private ExportTable ConversationsTable(ExportScope s) =>
        Table("aiConversations", "ai_conversations",
            _context.AiConversations.AsNoTracking().Where(c => c.UserId == s.UserId),
            c => c.Id,
            c => new ExportAiConversationDto(
                c.Id, c.SessionId, c.Context, c.Title, c.StartedAt, c.LastMessageAt, c.IsActive));

    private ExportTable MessagesTable(ExportScope s, int? conversationId = null)
    {
        var userId = s.UserId;
        var query = _context.AiConversationMessages.AsNoTracking()
            .Where(m => _context.AiConversations.Any(c => c.Id == m.ConversationId && c.UserId == userId));
        if (conversationId != null)
        {
            var cid = conversationId.Value;
            query = query.Where(m => m.ConversationId == cid);
        }
        return Table("messages", "ai_messages", query, m => m.Id,
            m => new ExportAiMessageDto(m.Id, m.ConversationId, m.Role, m.Content, m.Model, m.CreatedAt));
    }

    /// <summary>
    /// All of the user's messages ordered by (ConversationId, Id), paged with a composite keyset so the
    /// JSON writer can merge them into the conversations list in a single pass.
    /// </summary>
    private async IAsyncEnumerable<ExportAiMessageDto> MessagesByConversation(
        ExportScope s, [EnumeratorCancellation] CancellationToken ct)
    {
        var userId = s.UserId;
        var query = _context.AiConversationMessages.AsNoTracking()
            .Where(m => _context.AiConversations.Any(c => c.Id == m.ConversationId && c.UserId == userId));
        var lastConversation = 0;
        var lastId = 0;
        while (true)
        {
            var afterConversation = lastConversation;
            var afterId = lastId;
            var page = await query
                .Where(m => m.ConversationId > afterConversation
                    || (m.ConversationId == afterConversation && m.Id > afterId))
                .OrderBy(m => m.ConversationId).ThenBy(m => m.Id)
                .Take(_pageSize)
                .ToListAsync(ct);
            foreach (var m in page)
            {
                yield return new ExportAiMessageDto(m.Id, m.ConversationId, m.Role, m.Content, m.Model, m.CreatedAt);
            }
            if (page.Count < _pageSize)
            {
                yield break;
            }
            lastConversation = page[^1].ConversationId;
            lastId = page[^1].Id;
        }
    }

    private static async IAsyncEnumerable<object> ToAsync(IEnumerable<object> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    // ---- JSON ----

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        options.Converters.Add(new IsoDateTimeConverter());
        options.Converters.Add(new IsoNullableDateTimeConverter());
        return options;
    }

    public async Task WriteJsonAsync(ExportScope scope, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteString("exportedAt", FormatDate(DateTime.UtcNow));
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WritePropertyName("user");
        JsonSerializer.Serialize(writer, scope.User, JsonOptions);

        foreach (var table in BuildTables(scope))
        {
            writer.WritePropertyName(table.JsonName);
            writer.WriteStartArray();
            var count = 0;
            await foreach (var row in table.Rows(cancellationToken))
            {
                JsonSerializer.Serialize(writer, row, table.RowType, JsonOptions);
                if (++count % 200 == 0)
                {
                    await writer.FlushAsync(cancellationToken);
                }
            }
            writer.WriteEndArray();
            await writer.FlushAsync(cancellationToken);
        }

        // Conversations carry their messages nested, merged from one paged message stream.
        writer.WritePropertyName("aiConversations");
        writer.WriteStartArray();
        await using var messageEnumerator = MessagesByConversation(scope, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var pendingMessage = await messageEnumerator.MoveNextAsync() ? messageEnumerator.Current : null;
        await foreach (var row in ConversationsTable(scope).Rows(cancellationToken))
        {
            var conversation = (ExportAiConversationDto)row;
            writer.WriteStartObject();
            using (var element = JsonSerializer.SerializeToDocument(conversation, JsonOptions))
            {
                foreach (var property in element.RootElement.EnumerateObject())
                {
                    property.WriteTo(writer);
                }
            }
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            var count = 0;
            // One merged pass over the messages (ordered by conversation, then id), not a query per chat.
            while (pendingMessage != null && pendingMessage.ConversationId == conversation.Id)
            {
                JsonSerializer.Serialize(writer, pendingMessage, typeof(ExportAiMessageDto), JsonOptions);
                if (++count % 200 == 0)
                {
                    await writer.FlushAsync(cancellationToken);
                }
                pendingMessage = await messageEnumerator.MoveNextAsync() ? messageEnumerator.Current : null;
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("preferences");
        JsonSerializer.Serialize(writer, scope.Preferences, JsonOptions);
        writer.WritePropertyName("family");
        JsonSerializer.Serialize(writer, scope.Family, JsonOptions);

        // Names of what is deliberately left out. No values.
        writer.WritePropertyName("excluded");
        writer.WriteStartArray();
        foreach (var category in ExcludedCategories)
        {
            writer.WriteStringValue(category);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    // ---- CSV zip ----

    public async Task WriteCsvZipAsync(ExportScope scope, Stream destination, CancellationToken cancellationToken = default)
    {
        // leaveOpen so the caller owns the stream; Dispose writes the zip central directory.
        using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteTableAsync(zip, "user", typeof(ExportUserDto), ToAsync(new object[] { scope.User }), cancellationToken);
            foreach (var table in BuildTables(scope))
            {
                await WriteTableAsync(zip, table.CsvName, table.RowType, table.Rows(cancellationToken), cancellationToken);
            }
            var conversations = ConversationsTable(scope);
            await WriteTableAsync(zip, conversations.CsvName, conversations.RowType, conversations.Rows(cancellationToken), cancellationToken);
            var messages = MessagesTable(scope);
            await WriteTableAsync(zip, messages.CsvName, messages.RowType, messages.Rows(cancellationToken), cancellationToken);

            var prefs = scope.Preferences;
            await WriteTableAsync(zip, "preferences_email", typeof(ExportEmailPreferencesDto),
                ToAsync(prefs.Email == null ? Array.Empty<object>() : new object[] { prefs.Email }), cancellationToken);
            await WriteTableAsync(zip, "preferences_push", typeof(ExportPushPreferencesDto),
                ToAsync(prefs.Push == null ? Array.Empty<object>() : new object[] { prefs.Push }), cancellationToken);
            await WriteTableAsync(zip, "preferences_mfa", typeof(ExportMfaDto),
                ToAsync(new object[] { prefs.Mfa }), cancellationToken);
            await WriteTableAsync(zip, "family", typeof(ExportFamilyDto),
                ToAsync(scope.Family == null ? Array.Empty<object>() : new object[] { scope.Family }), cancellationToken);
            await WriteTableAsync(zip, "family_members", typeof(ExportFamilyMemberDto),
                ToAsync((scope.Family?.Members ?? new List<ExportFamilyMemberDto>()).Cast<object>()), cancellationToken);

            await WriteExcludedAsync(zip);
        }
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task WriteExcludedAsync(ZipArchive zip)
    {
        var entry = zip.CreateEntry("EXCLUDED.md", CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
        await writer.WriteLineAsync("# What this export leaves out");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("These are deliberately not included, for your security or because they are not yours alone:");
        await writer.WriteLineAsync();
        foreach (var category in ExcludedCategories)
        {
            await writer.WriteLineAsync("- " + category);
        }
    }

    private static async Task WriteTableAsync(
        ZipArchive zip, string name, Type rowType, IAsyncEnumerable<object> rows, CancellationToken ct)
    {
        var entry = zip.CreateEntry($"{name}.csv", CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        // UTF-8 with a byte order mark so Excel reads accented text correctly (CSV entries only).
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(true), 16 * 1024);
        writer.NewLine = "\r\n";

        var props = CsvProperties(rowType);
        // The token goes to the writes too, so the deadline also covers a slow client, not just row enumeration.
        await writer.WriteLineAsync(string.Join(",", props.Select(p => CsvCell(ToCamel(p.Name)))).AsMemory(), ct);
        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.WriteLineAsync(
                string.Join(",", props.Select(p => CsvCell(FormatValue(p.GetValue(row))))).AsMemory(), ct);
        }
    }

    private static PropertyInfo[] CsvProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.PropertyType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(p.PropertyType))
            .ToArray();

    private static string ToCamel(string s) => JsonNamingPolicy.CamelCase.ConvertName(s);

    private static string FormatValue(object? value) => value switch
    {
        null => "",
        string s => GuardFormula(s),
        DateTime dt => FormatDate(dt),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        Guid g => g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => GuardFormula(value.ToString() ?? ""),
    };

    /// <summary>
    /// Spreadsheet formula-injection guard: text cells starting with = + - @ tab CR or LF get a
    /// leading single quote so Excel and Sheets treat them as text. Applied to strings only, so
    /// negative numbers stay numeric.
    /// </summary>
    public static string GuardFormula(string s) =>
        s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') ? "'" + s : s;

    /// <summary>RFC 4180 field quoting.</summary>
    public static string CsvCell(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    internal static string FormatDate(DateTime dt)
    {
        var utc = dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }

    private sealed class IsoDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTime();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatDate(value));
    }

    private sealed class IsoNullableDateTimeConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTime();

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteStringValue(FormatDate(value.Value));
            else writer.WriteNullValue();
        }
    }
}
