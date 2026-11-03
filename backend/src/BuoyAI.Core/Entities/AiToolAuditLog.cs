using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Audit log for all AI/MCP tool executions.
/// Provides security audit trail and analytics for AI usage.
/// </summary>
public class AiToolAuditLog
{
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// The MCP tool that was executed
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialized arguments passed to the tool
    /// </summary>
    [Required]
    public string ArgumentsJson { get; set; } = "{}";

    /// <summary>
    /// Whether the tool is read-only (no confirmation needed)
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Whether user confirmation was required
    /// </summary>
    public bool RequiredConfirmation { get; set; }

    /// <summary>
    /// The confirmation token used (if applicable)
    /// </summary>
    public Guid? ConfirmationToken { get; set; }

    /// <summary>
    /// Result of the tool execution (truncated if too long)
    /// </summary>
    public string? ResultJson { get; set; }

    /// <summary>
    /// Whether the execution was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if execution failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// How long the tool took to execute (milliseconds)
    /// </summary>
    public int ExecutionTimeMs { get; set; }

    /// <summary>
    /// The AI conversation session this was part of (if applicable)
    /// </summary>
    public Guid? SessionId { get; set; }

    /// <summary>
    /// IP address of the request (for security auditing)
    /// </summary>
    [MaxLength(45)]
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent of the request
    /// </summary>
    [MaxLength(500)]
    public string? UserAgent { get; set; }

    public DateTime ExecutedAt { get; set; }

    // Navigation property
    public User User { get; set; } = null!;
}
