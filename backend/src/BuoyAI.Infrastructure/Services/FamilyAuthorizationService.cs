using Microsoft.Extensions.Logging;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Centralized authorization service for family-aware RBAC.
/// 
/// Role Permission Matrix:
/// | Operation                      | Owner | BudgetAdmin | Member | ReadOnly |
/// |--------------------------------|-------|-------------|--------|----------|
/// | View all family data           | ✅    | ✅          | ✅     | ✅       |
/// | Edit own personal data         | ✅    | ✅          | ✅     | ❌       |
/// | Edit family budgets/goals      | ✅    | ✅          | ❌     | ❌       |
/// | Create family budgets/goals    | ✅    | ✅          | ❌     | ❌       |
/// | Allocate to family goals       | ✅    | ✅          | ✅     | ❌       |
/// | Connect/disconnect banks       | ✅    | ✅          | ❌     | ❌       |
/// | Categorize transactions        | ✅    | ✅          | ✅     | ❌       |
/// | Manage allowance budgets       | ✅    | ✅          | ❌     | ❌       |
/// | Manage family settings         | ✅    | ❌          | ❌     | ❌       |
/// | Manage billing                 | ✅    | ❌          | ❌     | ❌       |
/// </summary>
public class FamilyAuthorizationService : IFamilyAuthorizationService
{
    private readonly IUserRepository _userRepository;
    private readonly IFamilyMemberRepository _familyMemberRepository;
    private readonly IBudgetRepository _budgetRepository;
    private readonly ISavingsRepository _savingsRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly ILogger<FamilyAuthorizationService> _logger;

    public FamilyAuthorizationService(
        IUserRepository userRepository,
        IFamilyMemberRepository familyMemberRepository,
        IBudgetRepository budgetRepository,
        ISavingsRepository savingsRepository,
        ITransactionRepository transactionRepository,
        IAccountRepository accountRepository,
        ILogger<FamilyAuthorizationService> logger)
    {
        _userRepository = userRepository;
        _familyMemberRepository = familyMemberRepository;
        _budgetRepository = budgetRepository;
        _savingsRepository = savingsRepository;
        _transactionRepository = transactionRepository;
        _accountRepository = accountRepository;
        _logger = logger;
    }

    #region Budget Authorization

    public async Task<AuthorizationResult> CanAccessBudgetAsync(int userId, int budgetId, ResourcePermission permission)
    {
        var budget = await _budgetRepository.GetByIdAsync(budgetId);
        if (budget == null)
        {
            _logger.LogWarning("Budget {BudgetId} not found during authorization check by user {UserId}", budgetId, userId);
            return AuthorizationResult.Denied("Budget not found");
        }

        // Owner of the budget always has full access
        if (budget.UserId == userId)
        {
            return AuthorizationResult.Allowed();
        }

        var user = await _userRepository.GetByIdAsync(userId);
        var role = await GetUserRoleAsync(userId);

        // Check if user is in the same family as the budget
        if (budget.FamilyId == null || user?.FamilyId != budget.FamilyId)
        {
            // Check for explicit budget sharing
            var share = await _budgetRepository.GetBudgetShareAsync(budgetId, userId);
            if (share != null)
            {
                // Shared budget access
                if (permission == ResourcePermission.Read)
                    return AuthorizationResult.Allowed(role);
                if (permission == ResourcePermission.Edit && share.CanEdit)
                    return AuthorizationResult.Allowed(role);
                
                _logger.LogWarning("User {UserId} with shared access denied {Permission} on budget {BudgetId} (CanEdit={CanEdit})",
                    userId, permission, budgetId, share.CanEdit);
                return AuthorizationResult.Denied($"Budget share does not allow {permission}", role);
            }

            _logger.LogWarning("User {UserId} denied access to budget {BudgetId} - not in same family and no share",
                userId, budgetId);
            return AuthorizationResult.Denied("Budget does not belong to your family", role);
        }

        // User is in the same family as the budget - apply role-based permissions
        return ApplyRoleBasedBudgetPermission(userId, budgetId, permission, role, budget.Type);
    }

    private AuthorizationResult ApplyRoleBasedBudgetPermission(
        int userId, int budgetId, ResourcePermission permission, FamilyMemberRole? role, BudgetType budgetType)
    {
        // ReadOnly can only read
        if (role == FamilyMemberRole.ReadOnly)
        {
            if (permission == ResourcePermission.Read)
                return AuthorizationResult.Allowed(role);

            _logger.LogWarning("ReadOnly user {UserId} denied {Permission} on budget {BudgetId}",
                userId, permission, budgetId);
            return AuthorizationResult.Denied("ReadOnly members cannot modify budgets", role);
        }

        // Read is always allowed for non-ReadOnly family members
        if (permission == ResourcePermission.Read)
            return AuthorizationResult.Allowed(role);

        // For family budgets, only Owner and BudgetAdmin can edit/delete
        if (budgetType == BudgetType.Family)
        {
            if (role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin)
            {
                return AuthorizationResult.Allowed(role);
            }

            _logger.LogWarning("Member user {UserId} denied {Permission} on family budget {BudgetId}",
                userId, permission, budgetId);
            return AuthorizationResult.Denied("Only Owner and BudgetAdmin can modify family budgets", role);
        }

        // For allowance budgets, only Owner and BudgetAdmin can manage
        if (budgetType == BudgetType.Allowance)
        {
            if (role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin)
            {
                return AuthorizationResult.Allowed(role);
            }

            _logger.LogWarning("Member user {UserId} denied {Permission} on allowance budget {BudgetId}",
                userId, permission, budgetId);
            return AuthorizationResult.Denied("Only Owner and BudgetAdmin can modify allowance budgets", role);
        }

        // Personal budgets in family - Member can edit their own (checked earlier)
        _logger.LogWarning("User {UserId} denied {Permission} on personal budget {BudgetId} owned by another user",
            userId, permission, budgetId);
        return AuthorizationResult.Denied("Cannot modify another user's personal budget", role);
    }

    public async Task<AuthorizationResult> CanCreateFamilyBudgetAsync(int userId)
    {
        var role = await GetUserRoleAsync(userId);
        
        if (role == null)
        {
            return AuthorizationResult.Denied("User is not in a family");
        }

        if (role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin)
        {
            return AuthorizationResult.Allowed(role);
        }

        _logger.LogWarning("User {UserId} with role {Role} denied creating family budget", userId, role);
        return AuthorizationResult.Denied("Only Owner and BudgetAdmin can create family budgets", role);
    }

    public async Task ValidateBudgetAccessOrThrowAsync(int userId, int budgetId, ResourcePermission permission)
    {
        var result = await CanAccessBudgetAsync(userId, budgetId, permission);
        if (!result.IsAuthorized)
        {
            throw new UnauthorizedAccessException(result.Reason ?? "Access denied to budget");
        }
    }

    #endregion

    #region Savings Goal Authorization

    public async Task<AuthorizationResult> CanAccessGoalAsync(int userId, int goalId, ResourcePermission permission)
    {
        var goal = await _savingsRepository.GetByIdAsync(goalId);
        if (goal == null)
        {
            _logger.LogWarning("Savings goal {GoalId} not found during authorization check by user {UserId}", goalId, userId);
            return AuthorizationResult.Denied("Savings goal not found");
        }

        // Owner of the goal always has full access
        if (goal.UserId == userId)
        {
            return AuthorizationResult.Allowed();
        }

        var user = await _userRepository.GetByIdAsync(userId);
        var role = await GetUserRoleAsync(userId);

        // Check if this is a family goal and user is in the same family
        if (goal.FamilyId == null || user?.FamilyId != goal.FamilyId)
        {
            _logger.LogWarning("User {UserId} denied access to goal {GoalId} - not in same family",
                userId, goalId);
            return AuthorizationResult.Denied("Goal does not belong to your family", role);
        }

        // User is in the same family as the goal - apply role-based permissions
        return ApplyRoleBasedGoalPermission(userId, goalId, permission, role, goal.Type);
    }

    private AuthorizationResult ApplyRoleBasedGoalPermission(
        int userId, int goalId, ResourcePermission permission, FamilyMemberRole? role, SavingsGoalType goalType)
    {
        // ReadOnly can only read
        if (role == FamilyMemberRole.ReadOnly)
        {
            if (permission == ResourcePermission.Read)
                return AuthorizationResult.Allowed(role);

            _logger.LogWarning("ReadOnly user {UserId} denied {Permission} on goal {GoalId}",
                userId, permission, goalId);
            return AuthorizationResult.Denied("ReadOnly members cannot modify savings goals", role);
        }

        // Read is always allowed for non-ReadOnly family members
        if (permission == ResourcePermission.Read)
            return AuthorizationResult.Allowed(role);

        // Allocate permission is allowed for all non-ReadOnly family members
        if (permission == ResourcePermission.Allocate)
        {
            return AuthorizationResult.Allowed(role);
        }

        // For family goals, only Owner and BudgetAdmin can edit/delete
        if (goalType == SavingsGoalType.Family)
        {
            if (role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin)
            {
                return AuthorizationResult.Allowed(role);
            }

            _logger.LogWarning("Member user {UserId} denied {Permission} on family goal {GoalId}",
                userId, permission, goalId);
            return AuthorizationResult.Denied("Only Owner and BudgetAdmin can modify family savings goals", role);
        }

        // Personal goals - only owner can edit (checked earlier, this is a family member accessing)
        _logger.LogWarning("User {UserId} denied {Permission} on personal goal {GoalId} owned by another user",
            userId, permission, goalId);
        return AuthorizationResult.Denied("Cannot modify another user's personal savings goal", role);
    }

    public async Task<AuthorizationResult> CanCreateFamilyGoalAsync(int userId)
    {
        var role = await GetUserRoleAsync(userId);
        
        if (role == null)
        {
            return AuthorizationResult.Denied("User is not in a family");
        }

        if (role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin)
        {
            return AuthorizationResult.Allowed(role);
        }

        _logger.LogWarning("User {UserId} with role {Role} denied creating family goal", userId, role);
        return AuthorizationResult.Denied("Only Owner and BudgetAdmin can create family savings goals", role);
    }

    public async Task ValidateGoalAccessOrThrowAsync(int userId, int goalId, ResourcePermission permission)
    {
        var result = await CanAccessGoalAsync(userId, goalId, permission);
        if (!result.IsAuthorized)
        {
            throw new UnauthorizedAccessException(result.Reason ?? "Access denied to savings goal");
        }
    }

    #endregion

    #region Transaction Authorization

    public async Task<AuthorizationResult> CanAccessTransactionAsync(int userId, int transactionId, ResourcePermission permission)
    {
        var transaction = await _transactionRepository.GetByIdAsync(transactionId);
        if (transaction == null)
        {
            _logger.LogWarning("Transaction {TransactionId} not found during authorization check by user {UserId}", 
                transactionId, userId);
            return AuthorizationResult.Denied("Transaction not found");
        }

        var account = await _accountRepository.GetByIdAsync(transaction.AccountId);
        if (account == null)
        {
            _logger.LogWarning("Account {AccountId} not found for transaction {TransactionId}", 
                transaction.AccountId, transactionId);
            return AuthorizationResult.Denied("Transaction account not found");
        }

        // Owner of the account always has full access
        if (account.UserId == userId)
        {
            return AuthorizationResult.Allowed();
        }

        var user = await _userRepository.GetByIdAsync(userId);
        var role = await GetUserRoleAsync(userId);

        // Check if user is in the same family as the account
        if (account.FamilyId == null || user?.FamilyId != account.FamilyId)
        {
            _logger.LogWarning("User {UserId} denied access to transaction {TransactionId} - not in same family",
                userId, transactionId);
            return AuthorizationResult.Denied("Transaction does not belong to your family", role);
        }

        // User is in the same family - apply role-based permissions
        return ApplyRoleBasedTransactionPermission(userId, transactionId, permission, role);
    }

    private AuthorizationResult ApplyRoleBasedTransactionPermission(
        int userId, int transactionId, ResourcePermission permission, FamilyMemberRole? role)
    {
        // ReadOnly can only read
        if (role == FamilyMemberRole.ReadOnly)
        {
            if (permission == ResourcePermission.Read)
                return AuthorizationResult.Allowed(role);

            _logger.LogWarning("ReadOnly user {UserId} denied {Permission} on transaction {TransactionId}",
                userId, permission, transactionId);
            return AuthorizationResult.Denied("ReadOnly members cannot modify transactions", role);
        }

        // Non-ReadOnly family members can read and edit (categorize, claim) transactions
        if (permission == ResourcePermission.Read || permission == ResourcePermission.Edit)
        {
            return AuthorizationResult.Allowed(role);
        }

        // Delete is more restrictive - only account owner or Owner/BudgetAdmin
        if (permission == ResourcePermission.Delete)
        {
            if (role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin)
            {
                return AuthorizationResult.Allowed(role);
            }

            _logger.LogWarning("Member user {UserId} denied {Permission} on transaction {TransactionId}",
                userId, permission, transactionId);
            return AuthorizationResult.Denied("Only Owner and BudgetAdmin can delete family transactions", role);
        }

        // Deny unknown permission types for transactions
        _logger.LogWarning("User {UserId} denied unsupported permission {Permission} on transaction {TransactionId}",
            userId, permission, transactionId);
        return AuthorizationResult.Denied("Unsupported permission type for transactions", role);
    }

    public async Task ValidateTransactionAccessOrThrowAsync(int userId, int transactionId, ResourcePermission permission)
    {
        var result = await CanAccessTransactionAsync(userId, transactionId, permission);
        if (!result.IsAuthorized)
        {
            throw new UnauthorizedAccessException(result.Reason ?? "Access denied to transaction");
        }
    }

    #endregion

    #region Bank Connection Authorization

    public async Task<AuthorizationResult> CanManageBankConnectionsAsync(int userId)
    {
        var role = await GetUserRoleAsync(userId);

        // Users not in a family can always manage their own bank connections
        if (role == null)
        {
            return AuthorizationResult.Allowed();
        }

        // Only Owner and BudgetAdmin can connect/disconnect bank accounts
        if (role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin)
        {
            return AuthorizationResult.Allowed(role);
        }

        _logger.LogWarning("User {UserId} with role {Role} denied bank connection management", userId, role);
        return AuthorizationResult.Denied("Only Owner and BudgetAdmin can connect or disconnect bank accounts", role);
    }

    public async Task ValidateBankConnectionAccessOrThrowAsync(int userId)
    {
        var result = await CanManageBankConnectionsAsync(userId);
        if (!result.IsAuthorized)
        {
            throw new UnauthorizedAccessException(result.Reason ?? "Access denied to bank connection management");
        }
    }

    #endregion

    #region Role Queries

    public async Task<FamilyMemberRole?> GetUserRoleAsync(int userId)
    {
        var member = await _familyMemberRepository.GetByUserIdAsync(userId);
        return member?.Role;
    }

    public async Task<bool> CanModifyFinancialDataAsync(int userId)
    {
        var role = await GetUserRoleAsync(userId);
        
        // User not in family can always modify their own data
        if (role == null)
            return true;

        // ReadOnly cannot modify
        return role.Value != FamilyMemberRole.ReadOnly;
    }

    public async Task<bool> CanManageFamilyFinancesAsync(int userId)
    {
        var role = await GetUserRoleAsync(userId);
        
        if (role == null)
            return false;

        return role == FamilyMemberRole.Owner || role == FamilyMemberRole.BudgetAdmin;
    }

    public async Task<bool> AreUsersInSameFamilyAsync(int userId1, int userId2)
    {
        var user1 = await _userRepository.GetByIdAsync(userId1);
        var user2 = await _userRepository.GetByIdAsync(userId2);

        if (user1?.FamilyId == null || user2?.FamilyId == null)
            return false;

        return user1.FamilyId == user2.FamilyId;
    }

    #endregion
}
