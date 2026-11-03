using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

public interface IUserRepository
{
  Task<User?> GetByIdAsync(int id);
  Task<User?> GetByUsernameAsync(string username);
  Task<User?> GetByEmailAsync(string email);
  Task<User?> GetByGoogleIdAsync(string googleId);
  Task<User?> GetByAppleIdAsync(string appleId);
  Task<User> CreateAsync(User user);
  Task<User> UpdateAsync(User user);
  Task DeleteAsync(int id);
  Task<int> CountGoogleRegistrationsSinceAsync(DateTime since);
  Task<int> GetRecentlyActiveUserCountAsync(TimeSpan recentWindow);
}

public interface IAccountRepository
{
  Task<IEnumerable<Account>> GetByUserIdAsync(int userId);
  Task<IEnumerable<Account>> GetByFamilyIdAsync(int familyId);
  Task<IEnumerable<Account>> GetByUserOrFamilyAsync(int userId, int? familyId);
  Task<IEnumerable<Account>> GetByConnectionIdAsync(int connectionId);
  /// <summary>Active account counts per connection id, in one query and without loading transactions.</summary>
  Task<Dictionary<int, int>> CountActiveByConnectionIdsAsync(IReadOnlyCollection<int> connectionIds);
  Task<Account?> GetByIdAsync(int id);
  Task<Account?> GetByPlaidAccountIdAsync(string plaidAccountId);
  Task<Account> CreateAsync(Account account);
  Task<Account> UpdateAsync(Account account);
  Task DeleteAsync(int id);
}

public interface ITransactionRepository
{
  Task<IEnumerable<Transaction>> GetByAccountIdAsync(int accountId);
  Task<IEnumerable<Transaction>> GetByUserIdAsync(int userId, DateTime? startDate = null, DateTime? endDate = null);
  Task<IEnumerable<Transaction>> GetByUserOrFamilyAsync(int userId, int? familyId, DateTime? startDate = null, DateTime? endDate = null);
  Task<Transaction?> GetByIdAsync(int id);
  Task<Transaction?> GetByIdWithSplitsAsync(int id);
  Task<Transaction?> GetByPlaidTransactionIdAsync(string plaidTransactionId);
  Task<Transaction> CreateAsync(Transaction transaction);
  Task<Transaction> AddAsync(Transaction transaction);
  Task<Transaction> UpdateAsync(Transaction transaction);
  Task<IEnumerable<Transaction>> CreateManyAsync(IEnumerable<Transaction> transactions);
  /// <summary>Reassign an account's existing claims from one owner to another (null = family).
  /// Returns the number of transactions moved.</summary>
  Task<int> ReattributeAccountClaimsAsync(int accountId, int? fromUserId, int? toUserId);
  /// <summary>Clears NeedsReview on the given transactions in one round trip. Returns the row count.</summary>
  Task<int> ClearNeedsReviewAsync(IEnumerable<int> transactionIds);
  /// <summary>
  /// Expense transactions earmarked to any of the given goals whose draw has not been tied to a
  /// covering transfer. Includes the Account so titles and account names can be built.
  /// </summary>
  Task<IEnumerable<Transaction>> GetUncoveredGoalWithdrawalsAsync(IEnumerable<int> goalIds);
  Task DeleteAsync(int id);
  Task DeleteAllByUserIdAsync(int userId);
  /// <summary>Runs <paramref name="action"/> inside a single database transaction so a multi-step
  /// mutation (e.g. splitting a transaction: delete old children, insert new children, flag the
  /// parent) commits atomically or rolls back entirely on failure. Uses the provider execution
  /// strategy so it is retry-safe.</summary>
  Task ExecuteInTransactionAsync(Func<Task> action);
}

public interface IBudgetRepository
{
  // Basic operations
  Task<IEnumerable<Budget>> GetByUserIdAsync(int userId);
  Task<Budget?> GetByIdAsync(int id);
  Task<Budget?> GetActiveByUserIdAsync(int userId);
  Task<Budget?> GetActivePersonalBudgetAsync(int userId);
  Task<IEnumerable<Budget>> GetActiveBudgetsByTypeAsync(int userId, BudgetType type);
  Task<Budget> CreateAsync(Budget budget);
  Task<Budget> UpdateAsync(Budget budget);
  Task DeleteAsync(int id);
  Task<BudgetCategory?> GetBudgetCategoryAsync(int categoryId);
  Task<BudgetCategory> UpdateBudgetCategoryAsync(BudgetCategory category);
  
  // Multi-budget operations
  Task<Budget?> GetPrimaryBudgetAsync(int userId);
  Task<Budget?> GetPrimaryFamilyBudgetAsync(int familyId);
  Task<IEnumerable<Budget>> GetByFamilyIdAsync(int familyId);
  Task<IEnumerable<Budget>> GetAccessibleBudgetsAsync(int userId);
  Task<IEnumerable<Budget>> GetChildBudgetsAsync(int parentBudgetId);
  
  // Budget sharing
  Task<BudgetShare> CreateBudgetShareAsync(BudgetShare share);
  Task DeleteBudgetShareAsync(int budgetId, int sharedWithUserId);
  Task<IEnumerable<BudgetShare>> GetBudgetSharesAsync(int budgetId);
  Task<BudgetShare?> GetBudgetShareAsync(int budgetId, int sharedWithUserId);
  
  // Category operations
  Task<BudgetCategory> CreateBudgetCategoryAsync(BudgetCategory category);
  
  /// <summary>
  /// Get a budget category by its associated CategoryId.
  /// Used to check if a category is being used as an allowance.
  /// </summary>
  Task<BudgetCategory?> GetBudgetCategoryByCategoryIdAsync(int categoryId);

  /// <summary>
  /// Get the family "allowance" category that funds a given child budget, if any
  /// (the category with LinkedBudgetId == childBudgetId and IsAllowance == true).
  /// Used to detect linked Personal children and to bind their allocation to the
  /// family category (RFC #793).
  /// </summary>
  Task<BudgetCategory?> GetLinkedCategoryForChildAsync(int childBudgetId);

  /// <summary>
  /// Check if a user has access to a specific budget.
  /// Returns true if user is owner, family member with matching FamilyId, or has shared access.
  /// </summary>
  Task<bool> HasAccessAsync(int budgetId, int userId);
}

public interface IPlaidConnectionRepository
{
  Task<IEnumerable<PlaidConnection>> GetByUserIdAsync(int userId);
  Task<IEnumerable<PlaidConnection>> GetByUserOrFamilyAsync(int userId, int? familyId);
  Task<PlaidConnection?> GetByIdAsync(int id);
  Task<PlaidConnection?> GetByItemIdAsync(string itemId);
  Task<PlaidConnection> CreateAsync(PlaidConnection connection);
  Task<PlaidConnection> UpdateAsync(PlaidConnection connection);
  Task DeleteAsync(int id);
  Task<IEnumerable<PlaidConnection>> GetConnectionsNeedingBackgroundImport();
  Task<IEnumerable<PlaidConnection>> GetActiveConnectionsForSync();

    /// <summary>Cloud bank feed connections waiting for a sign-in that could clear at the Cloud on their own (LOGIN_REPAIRED).</summary>
    Task<IEnumerable<PlaidConnection>> GetBridgeConnectionsNeedingReconcileAsync();

  /// <summary>
  /// Atomically takes the right to send the "needs attention" email for a connection: sets
  /// <c>AlertSentAt</c> only where it is still null, touching no other column. True means this
  /// caller won and should send; false means another caller already did.
  /// </summary>
  Task<bool> TryClaimAlertEmailAsync(int connectionId, DateTime sentAtUtc);

  /// <summary>Gives the claim back (clears <c>AlertSentAt</c>) when the email could not be sent.</summary>
  Task ReleaseAlertEmailClaimAsync(int connectionId);

  /// <summary>
  /// Clears <c>AlertSentAt</c> only while the connection is still Active, in one conditional update,
  /// so a sync that finishes after a webhook flagged the connection cannot re-arm a second email.
  /// </summary>
  Task ClearAlertEmailClaimIfActiveAsync(int connectionId);
}

public interface ISubscriptionRepository
{
  Task<IEnumerable<Subscription>> GetByUserIdAsync(int userId);
  Task<Subscription?> GetByIdAsync(int id);
  Task<Subscription> CreateAsync(Subscription subscription);
  Task<Subscription> UpdateAsync(Subscription subscription);
  Task DeleteAsync(int id);
  Task<IEnumerable<Subscription>> GetByUserIdAndDateRangeAsync(int userId, DateTime startDate, DateTime endDate);
  Task<IEnumerable<Subscription>> GetActiveByUserIdAsync(int userId);
}

public interface ISubscriptionTransactionRepository
{
  Task<IEnumerable<SubscriptionTransaction>> GetBySubscriptionIdAsync(int subscriptionId);
  Task<SubscriptionTransaction> CreateAsync(SubscriptionTransaction subscriptionTransaction);
  Task DeleteBySubscriptionIdAsync(int subscriptionId);
  
  /// <summary>
  /// Get all transaction IDs that are linked to subscriptions for efficient batch lookup.
  /// Used to determine if transactions are recurring.
  /// </summary>
  Task<HashSet<int>> GetRecurringTransactionIdsAsync(IEnumerable<int> transactionIds);
}

public interface IDismissedSubscriptionPatternRepository
{
  Task<IEnumerable<DismissedSubscriptionPattern>> GetByUserIdAsync(int userId);
  Task<DismissedSubscriptionPattern?> GetByMerchantNameAsync(int userId, string merchantName);
  /// <summary>
  /// Check if a pattern is dismissed and still within the skip period (default 60 days).
  /// Patterns older than skipDays are no longer considered dismissed.
  /// </summary>
  Task<bool> IsPatternDismissedAsync(int userId, string merchantName, decimal? amount = null, int skipDays = 60);
  /// <summary>
  /// Batch variant of the dismissal check: returns every dismissal for the user that is still
  /// active (permanent, or within <paramref name="skipDays"/>). Callers evaluate merchant/amount
  /// matches in-memory, avoiding an N+1 of <see cref="IsPatternDismissedAsync"/> per merchant group.
  /// </summary>
  Task<IReadOnlyList<DismissedSubscriptionPattern>> GetActiveDismissedAsync(int userId, int skipDays = 60);
  Task<DismissedSubscriptionPattern> CreateAsync(DismissedSubscriptionPattern pattern);
  Task DeleteAsync(int id);
  Task DeleteByUserIdAsync(int userId);
  /// <summary>
  /// Check if a pattern is permanently dismissed (ignores skip period).
  /// </summary>
  Task<bool> IsPermanentlyDismissedAsync(int userId, string merchantName);
}

public interface ISuggestedSubscriptionRepository
{
  Task<IEnumerable<SuggestedSubscription>> GetByUserIdAsync(int userId);
  Task<IEnumerable<SuggestedSubscription>> GetPendingByUserIdAsync(int userId);
  Task<SuggestedSubscription?> GetByIdAsync(int id);
  Task<SuggestedSubscription?> GetByMerchantAsync(int userId, string normalizedMerchantName);
  Task<SuggestedSubscription> CreateAsync(SuggestedSubscription suggestion);
  Task<SuggestedSubscription> UpdateAsync(SuggestedSubscription suggestion);
  Task DeleteAsync(int id);
  Task DeleteByUserIdAsync(int userId);
  Task<int> GetSuggestionCountSinceAsync(int userId, DateTime since);
  /// <summary>
  /// Auto-expire pending suggestions older than the given age by marking them dismissed.
  /// Returns the number of expired suggestions.
  /// </summary>
  Task<int> ExpireStaleSuggestionsAsync(TimeSpan maxAge);
}

public interface ICrowdsourcedSubscriptionPatternRepository
{
  Task<CrowdsourcedSubscriptionPattern?> GetByMerchantNameAsync(string normalizedMerchantName);
  /// <summary>
  /// Batch variant of <see cref="GetByMerchantNameAsync"/>: fetches every crowdsourced pattern for
  /// the given normalized merchant names in one query, keyed by normalized name. Avoids an N+1 when
  /// detecting subscriptions across many merchant groups.
  /// </summary>
  Task<IReadOnlyDictionary<string, CrowdsourcedSubscriptionPattern>> GetByMerchantNamesAsync(IEnumerable<string> normalizedMerchantNames);
  Task<CrowdsourcedSubscriptionPattern> CreateOrUpdateAsync(string normalizedMerchantName, decimal amount, int frequency, bool isConfirmation);
  Task<IEnumerable<CrowdsourcedSubscriptionPattern>> GetTopPatternsAsync(int count = 100);
  Task RecalculateConfidenceBoostsAsync();
}

public interface IUserConsentRepository
{
  Task<IEnumerable<UserConsent>> GetByUserIdAsync(int userId);
  Task<UserConsent?> GetByIdAsync(int id);
  Task<UserConsent?> GetLatestConsentAsync(int userId, string consentType);
  Task<UserConsent> CreateAsync(UserConsent consent);
  Task<IEnumerable<UserConsent>> GetByConsentTypeAsync(string consentType);
}

public interface IOnboardingQuizRepository
{
  Task<UserOnboardingQuiz?> GetByUserIdAsync(int userId);
  Task<UserOnboardingQuiz?> GetLatestByUserIdAsync(int userId);
  Task<UserOnboardingQuiz> CreateAsync(UserOnboardingQuiz quiz);
  Task<UserOnboardingQuiz> UpdateAsync(UserOnboardingQuiz quiz);
  Task<IEnumerable<UserOnboardingQuiz>> GetVersionHistoryByUserIdAsync(int userId);
  Task<bool> HasCompletedQuizAsync(int userId);
}

public interface IFamilyRepository
{
  Task<Family?> GetByIdAsync(int id);
  Task<Family?> GetByUserIdAsync(int userId);
  Task<Family> CreateAsync(Family family);
  Task<Family> UpdateAsync(Family family);
  Task DeleteAsync(int id);
  Task<IEnumerable<FamilyMember>> GetMembersAsync(int familyId);
  Task<FamilyMember?> GetMemberAsync(int familyId, int userId);
  Task<bool> IsUserInFamilyAsync(int userId, int familyId);
}

public interface IFamilyInvitationRepository
{
  Task<FamilyInvitation?> GetByIdAsync(int id);
  Task<FamilyInvitation?> GetByTokenAsync(string token);
  /// <summary>Finds an invitation by token whatever its state (pending, accepted or expired).</summary>
  Task<FamilyInvitation?> GetByTokenAnyStateAsync(string token);
  Task<IEnumerable<FamilyInvitation>> GetByFamilyIdAsync(int familyId);
  Task<IEnumerable<FamilyInvitation>> GetPendingByFamilyIdAsync(int familyId);
  Task<IEnumerable<FamilyInvitation>> GetByEmailAsync(string email);
  Task<FamilyInvitation> CreateAsync(FamilyInvitation invitation);
  Task<FamilyInvitation> UpdateAsync(FamilyInvitation invitation);
  Task DeleteAsync(int id);
  Task DeleteExpiredAsync();
}

public interface IFamilyMemberRepository
{
  Task<FamilyMember?> GetByIdAsync(int id);
  Task<FamilyMember?> GetByUserIdAsync(int userId);
  /// <summary>The user's membership row in this family, active or not (one row per person per family).</summary>
  Task<FamilyMember?> GetByUserAndFamilyAnyStateAsync(int userId, int familyId);
  Task<IEnumerable<FamilyMember>> GetByFamilyIdAsync(int familyId);
  Task<FamilyMember> CreateAsync(FamilyMember member);
  Task<FamilyMember> UpdateAsync(FamilyMember member);
  Task DeleteAsync(int id);
  Task RemoveMemberAsync(int familyId, int userId);
}

public interface ICategoryGroupRepository
{
  /// <summary>
  /// Get all system-defined category groups (UserId = null, FamilyId = null)
  /// </summary>
  Task<IEnumerable<CategoryGroup>> GetSystemGroupsAsync();
  
  /// <summary>
  /// Get all groups accessible to a user (system + user's custom + family's custom if applicable)
  /// </summary>
  Task<IEnumerable<CategoryGroup>> GetAccessibleGroupsAsync(int userId, int? familyId);
  
  /// <summary>
  /// Get a specific group by ID
  /// </summary>
  Task<CategoryGroup?> GetByIdAsync(int id);
  
  /// <summary>
  /// Create a new custom group
  /// </summary>
  Task<CategoryGroup> CreateAsync(CategoryGroup group);
  
  /// <summary>
  /// Update an existing group
  /// </summary>
  Task<CategoryGroup> UpdateAsync(CategoryGroup group);
  
  /// <summary>
  /// Delete a custom group (cannot delete system groups)
  /// </summary>
  Task DeleteAsync(int id);
  
  /// <summary>
  /// Get the default group ID for a given transaction category
  /// </summary>
  Task<int?> GetDefaultGroupIdForCategoryAsync(TransactionCategory category);
}
