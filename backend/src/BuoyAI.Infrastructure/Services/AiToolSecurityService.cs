using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing AI tool confirmation tokens and audit logging.
/// Provides security layer for AI write operations.
/// </summary>
public interface IAiToolSecurityService
{
    /// <summary>
    /// Create a pending confirmation for a write operation.
    /// Returns a token that must be provided to execute the action.
    /// </summary>
    Task<PendingToolConfirmation> CreatePendingConfirmationAsync(
        int userId, 
        string toolName, 
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate and consume a confirmation token.
    /// Returns the pending confirmation if valid, null if expired/used/invalid.
    /// </summary>
    Task<PendingToolConfirmation?> ValidateAndConsumeTokenAsync(
        int userId,
        Guid confirmationToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a confirmation as executed with result.
    /// </summary>
    Task UpdateConfirmationResultAsync(
        int confirmationId,
        string result,
        bool success,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a tool execution for audit purposes.
    /// </summary>
    Task LogToolExecutionAsync(
        int userId,
        string toolName,
        Dictionary<string, object>? arguments,
        bool isReadOnly,
        bool requiredConfirmation,
        Guid? confirmationToken,
        string? result,
        bool success,
        string? errorMessage,
        int executionTimeMs,
        Guid? sessionId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clean up expired confirmations (should be called periodically).
    /// </summary>
    Task CleanupExpiredConfirmationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit logs for a user (for admin/user viewing).
    /// </summary>
    Task<List<AiToolAuditLog>> GetAuditLogsAsync(
        int userId, 
        int limit = 100, 
        CancellationToken cancellationToken = default);
}

public class AiToolSecurityService : IAiToolSecurityService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<AiToolSecurityService> _logger;
    private static readonly TimeSpan ConfirmationExpiry = TimeSpan.FromMinutes(30);

    public AiToolSecurityService(
        BuoyAIDbContext context,
        ILogger<AiToolSecurityService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<PendingToolConfirmation> CreatePendingConfirmationAsync(
        int userId,
        string toolName,
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        var confirmation = new PendingToolConfirmation
        {
            UserId = userId,
            ConfirmationToken = Guid.NewGuid(),
            ToolName = toolName,
            ArgumentsJson = JsonSerializer.Serialize(arguments ?? new Dictionary<string, object>()),
            CreatedAt = now,
            ExpiresAt = now.Add(ConfirmationExpiry),
            IsUsed = false
        };

        _context.PendingToolConfirmations.Add(confirmation);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created pending confirmation {Token} for user {UserId}, tool {ToolName}, expires at {ExpiresAt}",
            confirmation.ConfirmationToken, userId, toolName, confirmation.ExpiresAt);

        return confirmation;
    }

    public async Task<PendingToolConfirmation?> ValidateAndConsumeTokenAsync(
        int userId,
        Guid confirmationToken,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var confirmation = await _context.PendingToolConfirmations
            .FirstOrDefaultAsync(c =>
                c.ConfirmationToken == confirmationToken &&
                c.UserId == userId &&
                !c.IsUsed &&
                c.ExpiresAt > now,
                cancellationToken);

        if (confirmation == null)
        {
            _logger.LogWarning(
                "Invalid or expired confirmation token {Token} for user {UserId}",
                confirmationToken, userId);
            return null;
        }

        // Mark as used to prevent replay
        confirmation.IsUsed = true;
        confirmation.UsedAt = now;
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Consumed confirmation token {Token} for user {UserId}, tool {ToolName}",
            confirmationToken, userId, confirmation.ToolName);

        return confirmation;
    }

    public async Task UpdateConfirmationResultAsync(
        int confirmationId,
        string result,
        bool success,
        CancellationToken cancellationToken = default)
    {
        var confirmation = await _context.PendingToolConfirmations
            .FindAsync(new object[] { confirmationId }, cancellationToken);

        if (confirmation != null)
        {
            confirmation.ExecutionResult = result.Length > 10000 
                ? result[..10000] + "...[truncated]" 
                : result;
            confirmation.ExecutionSuccess = success;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task LogToolExecutionAsync(
        int userId,
        string toolName,
        Dictionary<string, object>? arguments,
        bool isReadOnly,
        bool requiredConfirmation,
        Guid? confirmationToken,
        string? result,
        bool success,
        string? errorMessage,
        int executionTimeMs,
        Guid? sessionId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var auditLog = new AiToolAuditLog
        {
            UserId = userId,
            ToolName = toolName,
            ArgumentsJson = JsonSerializer.Serialize(arguments ?? new Dictionary<string, object>()),
            IsReadOnly = isReadOnly,
            RequiredConfirmation = requiredConfirmation,
            ConfirmationToken = confirmationToken,
            ResultJson = result?.Length > 10000 ? result[..10000] + "...[truncated]" : result,
            Success = success,
            ErrorMessage = errorMessage,
            ExecutionTimeMs = executionTimeMs,
            SessionId = sessionId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            ExecutedAt = DateTime.UtcNow
        };

        _context.AiToolAuditLogs.Add(auditLog);
        await _context.SaveChangesAsync(cancellationToken);

        if (!success)
        {
            _logger.LogWarning(
                "Tool execution failed: User={UserId}, Tool={ToolName}, Error={Error}",
                userId, toolName, errorMessage);
        }
    }

    public async Task CleanupExpiredConfirmationsAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24); // Keep for 24h for audit purposes

        var expiredCount = await _context.PendingToolConfirmations
            .Where(c => c.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (expiredCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired tool confirmations", expiredCount);
        }
    }

    public async Task<List<AiToolAuditLog>> GetAuditLogsAsync(
        int userId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return await _context.AiToolAuditLogs
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.ExecutedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
