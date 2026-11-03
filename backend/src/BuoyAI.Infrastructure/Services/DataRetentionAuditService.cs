using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using System.Text.Json;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for auditing and logging data retention operations for compliance
/// </summary>
public class DataRetentionAuditService : IDataRetentionAuditService
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<DataRetentionAuditService> _logger;

    public DataRetentionAuditService(
        BuoyAIDbContext context,
        ILogger<DataRetentionAuditService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task LogRetentionExecutionAsync(object retentionSummary)
    {
        try
        {
            var auditRecord = new DataRetentionAuditLog
            {
                Id = Guid.NewGuid(),
                ExecutedAt = DateTime.UtcNow,
                OperationType = "automated_retention_cleanup",
                Summary = JsonSerializer.Serialize(retentionSummary),
                ExecutedBy = "DataRetentionService",
                CreatedAt = DateTime.UtcNow
            };

            _context.DataRetentionAuditLogs.Add(auditRecord);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Logged retention execution audit record {AuditId}", auditRecord.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging retention execution audit");
            throw;
        }
    }

    public async Task LogUserDataDeletionAsync(int userId, string requestSource, string deletionType)
    {
        try
        {
            var auditRecord = new DataRetentionAuditLog
            {
                Id = Guid.NewGuid(),
                ExecutedAt = DateTime.UtcNow,
                OperationType = "user_data_deletion",
                UserId = userId,
                RequestSource = requestSource,
                DeletionType = deletionType,
                Summary = JsonSerializer.Serialize(new
                {
                    UserId = userId,
                    RequestSource = requestSource,
                    DeletionType = deletionType,
                    ExecutedAt = DateTime.UtcNow
                }),
                ExecutedBy = requestSource == "user_request" ? $"User_{userId}" : "DataRetentionService",
                CreatedAt = DateTime.UtcNow
            };

            _context.DataRetentionAuditLogs.Add(auditRecord);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Logged user data deletion audit record {AuditId} for user {UserId}", 
                auditRecord.Id, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging user data deletion audit for user {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<object>> GetRetentionAuditLogsAsync(DateTime fromDate, DateTime toDate)
    {
        try
        {
            var auditLogs = await _context.DataRetentionAuditLogs
                .Where(log => log.ExecutedAt >= fromDate && log.ExecutedAt <= toDate)
                .OrderByDescending(log => log.ExecutedAt)
                .Select(log => new
                {
                    log.Id,
                    log.ExecutedAt,
                    log.OperationType,
                    log.UserId,
                    log.RequestSource,
                    log.DeletionType,
                    log.ExecutedBy,
                    log.Summary
                })
                .ToListAsync();

            _logger.LogInformation("Retrieved {Count} retention audit logs from {FromDate} to {ToDate}",
                auditLogs.Count, fromDate, toDate);

            return auditLogs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving retention audit logs from {FromDate} to {ToDate}",
                fromDate, toDate);
            throw;
        }
    }
}
