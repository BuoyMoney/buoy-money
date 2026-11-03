using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.DTOs.Admin;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Admin dashboard endpoints for business metrics and user management.
/// Requires Admin role.
/// </summary>
[Authorize(Roles = "Admin")]
[EnableRateLimiting("api")]
public class AdminController : BaseController
{
    private readonly BuoyAIDbContext _dbContext;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        BuoyAIDbContext dbContext,
        ILogger<AdminController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Get overview dashboard metrics
    /// </summary>
    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview()
    {
        _logger.LogInformation("Admin {AdminId} requested dashboard overview", GetCurrentUserId());
        
        var now = DateTime.UtcNow;
        var sevenDaysAgo = now.AddDays(-7);
        var thirtyDaysAgo = now.AddDays(-30);

        // Total users
        var totalUsers = await _dbContext.Users.CountAsync();
        
        // Active trials
        var activeTrials = await _dbContext.Users
            .CountAsync(u => u.IsTrialActive && u.TrialEndDate > now);
        
        // Paid users (have a subscription tier set)
        var paidUsers = await _dbContext.Users
            .CountAsync(u => u.SubscriptionTier != null && !u.IsTrialActive);
        
        // Expired trials (used trial but no subscription)
        var churnedUsers = await _dbContext.Users
            .CountAsync(u => u.HasUsedTrial && !u.IsTrialActive && u.SubscriptionTier == null);
        
        // New user counts
        var newUsersLast7Days = await _dbContext.Users
            .CountAsync(u => u.CreatedAt >= sevenDaysAgo);
        var newUsersLast30Days = await _dbContext.Users
            .CountAsync(u => u.CreatedAt >= thirtyDaysAgo);
        
        // Plaid connection stats using PlaidItemStatus enum
        var totalPlaidConnections = await _dbContext.PlaidConnections
            .CountAsync(p => p.IsActive);
        var healthyConnections = await _dbContext.PlaidConnections
            .CountAsync(p => p.IsActive && p.Status == PlaidItemStatus.Active);
        var needsReauth = await _dbContext.PlaidConnections
            .CountAsync(p => p.Status == PlaidItemStatus.LoginRequired || p.Status == PlaidItemStatus.Error);
        
        // Calculate MRR from paid users
        // Standard: $10/month, Premium: $15/month
        var tierCounts = await _dbContext.Users
            .Where(u => u.SubscriptionTier != null && !u.IsTrialActive)
            .GroupBy(u => u.SubscriptionTier)
            .Select(g => new { Tier = g.Key, Count = g.Count() })
            .ToListAsync();
        
        var standardCount = tierCounts.FirstOrDefault(t => t.Tier == SubscriptionTier.Standard)?.Count ?? 0;
        var premiumCount = tierCounts.FirstOrDefault(t => t.Tier == SubscriptionTier.Premium)?.Count ?? 0;
        var adminCount = tierCounts.FirstOrDefault(t => t.Tier == SubscriptionTier.Admin)?.Count ?? 0;
        
        var mrr = (standardCount * 10m) + (premiumCount * 15m) + (adminCount * 50m);

        return Ok(new AdminOverviewDto
        {
            TotalUsers = totalUsers,
            ActiveTrials = activeTrials,
            PaidUsers = paidUsers,
            ChurnedUsers = churnedUsers,
            MRR = mrr,
            ActivePlaidConnections = totalPlaidConnections,
            HealthyConnections = healthyConnections,
            NeedsReauth = needsReauth,
            NewUsersLast7Days = newUsersLast7Days,
            NewUsersLast30Days = newUsersLast30Days,
            GeneratedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get paginated list of users with search and filtering
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] AdminUserSearchParams searchParams)
    {
        _logger.LogInformation("Admin {AdminId} searching users: {SearchTerm}", 
            GetCurrentUserId(), searchParams.SearchTerm);
        
        var query = _dbContext.Users.AsQueryable();
        
        // Apply search filter
        if (!string.IsNullOrWhiteSpace(searchParams.SearchTerm))
        {
            var term = searchParams.SearchTerm.ToLower();
            query = query.Where(u => 
                u.Email.ToLower().Contains(term) ||
                u.FirstName.ToLower().Contains(term) ||
                u.LastName.ToLower().Contains(term));
        }
        
        // Apply subscription tier filter
        if (!string.IsNullOrWhiteSpace(searchParams.SubscriptionTier) &&
            Enum.TryParse<SubscriptionTier>(searchParams.SubscriptionTier, true, out var tier))
        {
            query = query.Where(u => u.SubscriptionTier == tier);
        }
        
        // Apply subscription status filter (maps to trial/paid/none)
        if (!string.IsNullOrWhiteSpace(searchParams.SubscriptionStatus))
        {
            query = searchParams.SubscriptionStatus.ToLower() switch
            {
                "trialing" => query.Where(u => u.IsTrialActive),
                "active" => query.Where(u => u.SubscriptionTier != null && !u.IsTrialActive),
                "expired" => query.Where(u => u.HasUsedTrial && !u.IsTrialActive && u.SubscriptionTier == null),
                _ => query
            };
        }
        
        // Apply date filters
        if (searchParams.CreatedAfter.HasValue)
        {
            query = query.Where(u => u.CreatedAt >= searchParams.CreatedAfter.Value);
        }
        if (searchParams.CreatedBefore.HasValue)
        {
            query = query.Where(u => u.CreatedAt <= searchParams.CreatedBefore.Value);
        }
        
        // Get total count before pagination
        var totalCount = await query.CountAsync();
        
        // Apply sorting
        query = searchParams.SortBy?.ToLower() switch
        {
            "email" => searchParams.SortDescending 
                ? query.OrderByDescending(u => u.Email) 
                : query.OrderBy(u => u.Email),
            "name" => searchParams.SortDescending 
                ? query.OrderByDescending(u => u.LastName).ThenByDescending(u => u.FirstName)
                : query.OrderBy(u => u.LastName).ThenBy(u => u.FirstName),
            "subscriptiontier" => searchParams.SortDescending 
                ? query.OrderByDescending(u => u.SubscriptionTier)
                : query.OrderBy(u => u.SubscriptionTier),
            _ => searchParams.SortDescending 
                ? query.OrderByDescending(u => u.CreatedAt) 
                : query.OrderBy(u => u.CreatedAt)
        };
        
        // Apply pagination
        var page = Math.Max(1, searchParams.Page);
        var pageSize = Math.Clamp(searchParams.PageSize, 1, 100);
        
        var users = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new AdminUserSummaryDto
            {
                Id = u.Id,
                Email = u.Email,
                Name = $"{u.FirstName} {u.LastName}".Trim(),
                SubscriptionTier = u.SubscriptionTier != null ? u.SubscriptionTier.ToString()! : "None",
                SubscriptionStatus = u.IsTrialActive ? "Trialing" : (u.SubscriptionTier != null ? "Active" : "None"),
                TrialEndDate = u.TrialEndDate,
                CreatedAt = u.CreatedAt,
                LastLoginAt = null, // Not tracked in entity
                PlaidConnectionCount = u.PlaidConnections.Count(p => p.IsActive),
                HasMfaEnabled = u.HasMfaEnabled,
                FamilyId = u.FamilyId,
                FamilyRole = null // Would need to look up in FamilyMember
            })
            .ToListAsync();

        return Ok(new AdminPaginatedResult<AdminUserSummaryDto>
        {
            Items = users,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>
    /// Get recent signups for dashboard
    /// </summary>
    [HttpGet("recent-signups")]
    public async Task<IActionResult> GetRecentSignups([FromQuery] int count = 10)
    {
        count = Math.Clamp(count, 1, 50);
        
        var signups = await _dbContext.Users
            .OrderByDescending(u => u.CreatedAt)
            .Take(count)
            .Select(u => new AdminRecentSignupDto
            {
                Id = u.Id,
                Email = u.Email,
                Name = $"{u.FirstName} {u.LastName}".Trim(),
                CreatedAt = u.CreatedAt,
                SubscriptionTier = u.SubscriptionTier != null ? u.SubscriptionTier.ToString()! : "Trial"
            })
            .ToListAsync();

        return Ok(signups);
    }

    /// <summary>
    /// Get Plaid connection health overview
    /// </summary>
    [HttpGet("plaid/health")]
    public async Task<IActionResult> GetPlaidHealth()
    {
        var connections = await _dbContext.PlaidConnections
            .Where(p => p.IsActive)
            .Select(p => new
            {
                p.Status,
                p.InstitutionId,
                p.InstitutionName,
                p.LastSyncAt
            })
            .ToListAsync();
        
        var byInstitution = connections
            .GroupBy(c => new { c.InstitutionId, c.InstitutionName })
            .Select(g => new AdminPlaidInstitutionHealthDto
            {
                InstitutionId = g.Key.InstitutionId ?? "unknown",
                InstitutionName = g.Key.InstitutionName ?? "Unknown",
                ConnectionCount = g.Count(),
                HealthyCount = g.Count(c => c.Status == PlaidItemStatus.Active),
                NeedsReauthCount = g.Count(c => c.Status == PlaidItemStatus.LoginRequired || c.Status == PlaidItemStatus.Error)
            })
            .OrderByDescending(i => i.ConnectionCount)
            .Take(10)
            .ToList();
        
        return Ok(new AdminPlaidHealthDto
        {
            TotalConnections = connections.Count,
            HealthyConnections = connections.Count(c => c.Status == PlaidItemStatus.Active),
            NeedsReauth = connections.Count(c => c.Status == PlaidItemStatus.LoginRequired || c.Status == PlaidItemStatus.Error),
            SyncErrors = connections.Count(c => c.Status == PlaidItemStatus.Error),
            LastSuccessfulSync = connections.Max(c => (DateTime?)c.LastSyncAt),
            InstitutionBreakdown = byInstitution
        });
    }

    /// <summary>
    /// Get detailed information about a specific user
    /// </summary>
    [HttpGet("users/{userId}")]
    public async Task<IActionResult> GetUserDetail(int userId)
    {
        _logger.LogInformation("Admin {AdminId} viewing user {TargetUserId} details", 
            GetCurrentUserId(), userId);
        
        var user = await _dbContext.Users
            .Include(u => u.PlaidConnections.Where(p => p.IsActive))
            .Include(u => u.Budgets)
            .AsSplitQuery()
            .FirstOrDefaultAsync(u => u.Id == userId);
        
        if (user == null)
        {
            return NotFound(new { Message = "User not found" });
        }

        return Ok(new AdminUserDetailDto
        {
            Id = user.Id,
            Email = user.Email,
            Name = $"{user.FirstName} {user.LastName}".Trim(),
            SubscriptionTier = user.SubscriptionTier != null ? user.SubscriptionTier.ToString()! : "None",
            SubscriptionStatus = user.IsTrialActive ? "Trialing" : (user.SubscriptionTier != null ? "Active" : "None"),
            TrialEndDate = user.TrialEndDate,
            CreatedAt = user.CreatedAt,
            LastLoginAt = null,
            PlaidConnectionCount = user.PlaidConnections.Count,
            HasMfaEnabled = user.HasMfaEnabled,
            FamilyId = user.FamilyId,
            FamilyRole = null,
            StripeCustomerId = user.StripeCustomerId,
            PlaidConnections = user.PlaidConnections.Select(p => new AdminPlaidConnectionDto
            {
                Id = p.Id,
                InstitutionName = p.InstitutionName ?? "Unknown",
                Status = p.Status.ToString(),
                LastSyncAt = p.LastSyncAt,
                CreatedAt = p.CreatedAt,
                AccountCount = 0, // Account count requires separate query
                NeedsReauth = p.Status == PlaidItemStatus.LoginRequired || p.Status == PlaidItemStatus.Error
            }).ToList(),
            Budgets = user.Budgets.Select(b => new AdminBudgetSummaryDto
            {
                Id = b.Id,
                Name = b.Name,
                TotalBudget = b.TotalIncome, // Use TotalIncome as budget amount
                IsActive = b.IsActive,
                Period = b.Period.ToString()
            }).ToList()
        });
    }
}
