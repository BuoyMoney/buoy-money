using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for data retention operations and user data deletion
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DataRetentionController : BaseController
{
    private readonly IDataRetentionManager _retentionManager;
    private readonly IDataRetentionAuditService _auditService;
    private readonly IAuthService _authService;
    private readonly ILogger<DataRetentionController> _logger;

    public DataRetentionController(
        IDataRetentionManager retentionManager,
        IDataRetentionAuditService auditService,
        IAuthService authService,
        ILogger<DataRetentionController> logger)
    {
        _retentionManager = retentionManager;
        _auditService = auditService;
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// Delete current user's account and all associated data
    /// </summary>
    [HttpDelete("account")]
    public async Task<IActionResult> DeleteUserAccount()
    {
        var userId = GetCurrentUserId();

        _logger.LogInformation("User {UserId} requested account deletion", userId);

        // Delegate to the full deletion orchestration so this endpoint behaves identically to
        // DELETE /user/account: cancel + delete the Stripe customer, revoke Plaid items at Plaid,
        // dissolve owned families, then purge. Calling the retention manager directly here would
        // skip that third-party revocation.
        var deleted = await _authService.DeleteAccountAsync(userId);

        if (!deleted)
        {
            _logger.LogError("Account deletion failed for user {UserId}", userId);
            return StatusCode(500, new { message = "Data deletion could not be completed" });
        }

        _logger.LogInformation("User {UserId} account deletion completed successfully", userId);

        return Ok(new {
            message = "Account and all data have been permanently deleted",
            deletedAt = DateTime.UtcNow,
            userId = userId
        });
    }

    /// <summary>
    /// Request specific data deletion (partial deletion)
    /// </summary>
    [HttpPost("delete-request")]
    public async Task<IActionResult> RequestDataDeletion([FromBody] DataDeletionRequest request)
    {
        var userId = GetCurrentUserId();

        _logger.LogInformation("User {UserId} requested partial data deletion: {DataTypes}",
            userId, string.Join(", ", request.DataTypes));

        // Log the deletion request for audit purposes
        await _auditService.LogUserDataDeletionAsync(userId, "user_request", "partial_deletion");

        // For now, return accepted - in production, this might queue the deletion
        return Accepted(new {
            message = "Data deletion request received and will be processed within 30 days",
            requestId = Guid.NewGuid(),
            dataTypes = request.DataTypes,
            requestedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Get data retention audit logs (admin only)
    /// </summary>
    [HttpGet("audit-logs")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var from = fromDate ?? DateTime.UtcNow.AddDays(-30);
        var to = toDate ?? DateTime.UtcNow;

        var auditLogs = await _auditService.GetRetentionAuditLogsAsync(from, to);

        return Ok(new {
            fromDate = from,
            toDate = to,
            logs = auditLogs
        });
    }

    /// <summary>
    /// Manually trigger data retention cleanup (admin only)
    /// </summary>
    [HttpPost("cleanup")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> TriggerManualCleanup()
    {
        var summary = new
        {
            DeletedClosedAccounts = await _retentionManager.CleanupClosedUserAccountsAsync(),
            DeletedSecurityLogs = await _retentionManager.CleanupSecurityLogsAsync(),
            DeletedErrorLogs = await _retentionManager.CleanupErrorLogsAsync(),
            DeletedAIInsights = await _retentionManager.CleanupOldAIInsightsAsync(),
            DeletedAIRefreshData = await _retentionManager.CleanupAIRefreshUsageAsync(),
            DeletedDisconnectedTransactions = await _retentionManager.CleanupDisconnectedAccountDataAsync(),
            DeletedExpiredTokens = await _retentionManager.CleanupExpiredPlaidTokensAsync(),
            RemovedChurnedPlaidItems = await _retentionManager.CleanupChurnedAccountPlaidItemsAsync(),
            ExecutedAt = DateTime.UtcNow
        };

        await _auditService.LogRetentionExecutionAsync(summary);

        _logger.LogInformation("Manual data retention cleanup completed: {@Summary}", summary);

        return Ok(new {
            message = "Data retention cleanup completed successfully",
            summary = summary
        });
    }
}

/// <summary>
/// Request model for partial data deletion
/// </summary>
public class DataDeletionRequest
{
    public List<string> DataTypes { get; set; } = new List<string>();
    public string? Reason { get; set; }
}
