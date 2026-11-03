using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Stores pending AI tool confirmations with secure tokens.
/// Prevents replay attacks and ensures user explicitly confirms each action.
/// </summary>
public class PendingToolConfirmation
{
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// Unique confirmation token (GUID) sent to frontend.
    /// Must be provided to confirm the action.
    /// </summary>
    [Required]
    public Guid ConfirmationToken { get; set; }

    /// <summary>
    /// The MCP tool name to execute (e.g., "create_budget_category")
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialized arguments for the tool
    /// </summary>
    [Required]
    public string ArgumentsJson { get; set; } = "{}";

    /// <summary>
    /// When this confirmation was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this confirmation expires (default: 5 minutes from creation)
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Whether this confirmation has been used (to prevent replay)
    /// </summary>
    public bool IsUsed { get; set; } = false;

    /// <summary>
    /// When the confirmation was used (if applicable)
    /// </summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>
    /// Result of the tool execution (if confirmed and executed)
    /// </summary>
    public string? ExecutionResult { get; set; }

    /// <summary>
    /// Whether the execution was successful
    /// </summary>
    public bool? ExecutionSuccess { get; set; }

    // Navigation property
    public User User { get; set; } = null!;
}
