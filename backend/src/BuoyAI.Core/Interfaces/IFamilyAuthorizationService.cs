using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Permission types for RBAC operations
/// </summary>
public enum ResourcePermission
{
    /// <summary>View/read access to a resource</summary>
    Read = 0,
    
    /// <summary>Create new instances of a resource</summary>
    Create = 1,
    
    /// <summary>Edit/update an existing resource</summary>
    Edit = 2,
    
    /// <summary>Delete a resource</summary>
    Delete = 3,
    
    /// <summary>Allocate funds to a savings goal</summary>
    Allocate = 4
}

/// <summary>
/// Result of an authorization check with details for logging/debugging
/// </summary>
public class AuthorizationResult
{
    public bool IsAuthorized { get; init; }
    public string? Reason { get; init; }
    public FamilyMemberRole? UserRole { get; init; }
    
    public static AuthorizationResult Allowed(FamilyMemberRole? role = null) => new()
    {
        IsAuthorized = true,
        UserRole = role
    };
    
    public static AuthorizationResult Denied(string reason, FamilyMemberRole? role = null) => new()
    {
        IsAuthorized = false,
        Reason = reason,
        UserRole = role
    };
}

/// <summary>
/// Centralized authorization service for family-aware RBAC.
/// Enforces role-based permissions across all financial operations.
/// 
/// Role Permission Matrix:
/// - Owner: Full access to everything (family management, billing, all data)
/// - BudgetAdmin: Can manage family budgets/goals, connect banks, but not billing/family settings
/// - Member: Can edit own data, allocate to family goals, categorize transactions
/// - ReadOnly: View-only access to family data (typically children)
/// </summary>
public interface IFamilyAuthorizationService
{
    #region Budget Authorization
    
    /// <summary>
    /// Check if user can perform an operation on a budget.
    /// </summary>
    Task<AuthorizationResult> CanAccessBudgetAsync(int userId, int budgetId, ResourcePermission permission);
    
    /// <summary>
    /// Check if user can create a family budget.
    /// Only Owner and BudgetAdmin can create family budgets.
    /// </summary>
    Task<AuthorizationResult> CanCreateFamilyBudgetAsync(int userId);
    
    /// <summary>
    /// Validate access and throw UnauthorizedAccessException if denied.
    /// </summary>
    Task ValidateBudgetAccessOrThrowAsync(int userId, int budgetId, ResourcePermission permission);
    
    #endregion
    
    #region Savings Goal Authorization
    
    /// <summary>
    /// Check if user can perform an operation on a savings goal.
    /// </summary>
    Task<AuthorizationResult> CanAccessGoalAsync(int userId, int goalId, ResourcePermission permission);
    
    /// <summary>
    /// Check if user can create a family savings goal.
    /// Only Owner and BudgetAdmin can create family goals.
    /// </summary>
    Task<AuthorizationResult> CanCreateFamilyGoalAsync(int userId);
    
    /// <summary>
    /// Validate access and throw UnauthorizedAccessException if denied.
    /// </summary>
    Task ValidateGoalAccessOrThrowAsync(int userId, int goalId, ResourcePermission permission);
    
    #endregion
    
    #region Transaction Authorization
    
    /// <summary>
    /// Check if user can perform an operation on a transaction.
    /// </summary>
    Task<AuthorizationResult> CanAccessTransactionAsync(int userId, int transactionId, ResourcePermission permission);
    
    /// <summary>
    /// Validate access and throw UnauthorizedAccessException if denied.
    /// </summary>
    Task ValidateTransactionAccessOrThrowAsync(int userId, int transactionId, ResourcePermission permission);
    
    #endregion
    
    #region Bank Connection Authorization
    
    /// <summary>
    /// Check if user can connect/disconnect bank accounts.
    /// Only Owner and BudgetAdmin can manage bank connections.
    /// </summary>
    Task<AuthorizationResult> CanManageBankConnectionsAsync(int userId);
    
    /// <summary>
    /// Validate bank connection access and throw UnauthorizedAccessException if denied.
    /// </summary>
    Task ValidateBankConnectionAccessOrThrowAsync(int userId);
    
    #endregion
    
    #region Role Queries
    
    /// <summary>
    /// Get the user's role in their family, or null if not in a family.
    /// </summary>
    Task<FamilyMemberRole?> GetUserRoleAsync(int userId);
    
    /// <summary>
    /// Check if user can modify any financial data (not ReadOnly).
    /// </summary>
    Task<bool> CanModifyFinancialDataAsync(int userId);
    
    /// <summary>
    /// Check if user can manage family budgets and goals (Owner or BudgetAdmin).
    /// </summary>
    Task<bool> CanManageFamilyFinancesAsync(int userId);
    
    /// <summary>
    /// Check if user is in the same family as another user.
    /// </summary>
    Task<bool> AreUsersInSameFamilyAsync(int userId1, int userId2);
    
    #endregion
}
