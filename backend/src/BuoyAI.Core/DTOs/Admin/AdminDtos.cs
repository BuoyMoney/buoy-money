namespace BuoyAI.Core.DTOs.Admin;

/// <summary>
/// Overview dashboard metrics for admin users
/// </summary>
public record AdminOverviewDto
{
    /// <summary>Total registered users</summary>
    public int TotalUsers { get; init; }
    
    /// <summary>Users currently in trial period</summary>
    public int ActiveTrials { get; init; }
    
    /// <summary>Users with active paid subscriptions</summary>
    public int PaidUsers { get; init; }
    
    /// <summary>Users who churned (cancelled/expired)</summary>
    public int ChurnedUsers { get; init; }
    
    /// <summary>Monthly Recurring Revenue in dollars</summary>
    public decimal MRR { get; init; }
    
    /// <summary>Active Plaid bank connections</summary>
    public int ActivePlaidConnections { get; init; }
    
    /// <summary>Plaid connections in healthy state</summary>
    public int HealthyConnections { get; init; }
    
    /// <summary>Plaid connections needing re-authentication</summary>
    public int NeedsReauth { get; init; }
    
    /// <summary>New users in the last 7 days</summary>
    public int NewUsersLast7Days { get; init; }
    
    /// <summary>New users in the last 30 days</summary>
    public int NewUsersLast30Days { get; init; }
    
    /// <summary>When this data was generated</summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// User summary for admin user list
/// </summary>
public record AdminUserSummaryDto
{
    public int Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SubscriptionTier { get; init; } = string.Empty;
    public string SubscriptionStatus { get; init; } = string.Empty;
    public DateTime? TrialEndDate { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public int PlaidConnectionCount { get; init; }
    public bool HasMfaEnabled { get; init; }
    public int? FamilyId { get; init; }
    public string? FamilyRole { get; init; }
}

/// <summary>
/// Detailed user information for admin view
/// </summary>
public record AdminUserDetailDto : AdminUserSummaryDto
{
    public string? StripeCustomerId { get; init; }
    public List<AdminPlaidConnectionDto> PlaidConnections { get; init; } = new();
    public List<AdminBudgetSummaryDto> Budgets { get; init; } = new();
}

/// <summary>
/// Plaid connection summary for admin view
/// </summary>
public record AdminPlaidConnectionDto
{
    public int Id { get; init; }
    public string InstitutionName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? LastSyncAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public int AccountCount { get; init; }
    public bool NeedsReauth { get; init; }
}

/// <summary>
/// Budget summary for admin view
/// </summary>
public record AdminBudgetSummaryDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public decimal TotalBudget { get; init; }
    public bool IsActive { get; init; }
    public string Period { get; init; } = string.Empty;
}

/// <summary>
/// Paginated result for admin queries
/// </summary>
public record AdminPaginatedResult<T>
{
    public List<T> Items { get; init; } = new();
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

/// <summary>
/// User search parameters for admin
/// </summary>
public record AdminUserSearchParams
{
    public string? SearchTerm { get; init; }
    public string? SubscriptionTier { get; init; }
    public string? SubscriptionStatus { get; init; }
    public DateTime? CreatedAfter { get; init; }
    public DateTime? CreatedBefore { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string SortBy { get; init; } = "CreatedAt";
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// Plaid health overview for admin
/// </summary>
public record AdminPlaidHealthDto
{
    public int TotalConnections { get; init; }
    public int HealthyConnections { get; init; }
    public int NeedsReauth { get; init; }
    public int SyncErrors { get; init; }
    public DateTime? LastSuccessfulSync { get; init; }
    public List<AdminPlaidInstitutionHealthDto> InstitutionBreakdown { get; init; } = new();
}

/// <summary>
/// Per-institution health stats
/// </summary>
public record AdminPlaidInstitutionHealthDto
{
    public string InstitutionId { get; init; } = string.Empty;
    public string InstitutionName { get; init; } = string.Empty;
    public int ConnectionCount { get; init; }
    public int HealthyCount { get; init; }
    public int NeedsReauthCount { get; init; }
}

/// <summary>
/// Recent signup for admin dashboard
/// </summary>
public record AdminRecentSignupDto
{
    public int Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string SubscriptionTier { get; init; } = string.Empty;
}
