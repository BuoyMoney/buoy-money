using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Logs individual MCP tool executions for auditing and feedback.
/// UserId is denormalized for Row-Level Security (RLS) enforcement.
/// </summary>
public class AiToolExecution
{
    public int Id { get; set; }

    /// <summary>
    /// The message that triggered this tool execution.
    /// </summary>
    [Required]
    public int MessageId { get; set; }

    /// <summary>
    /// Denormalized user ID for RLS policy enforcement.
    /// </summary>
    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// Denormalized session ID for efficient querying.
    /// </summary>
    [Required]
    public Guid SessionId { get; set; }

    /// <summary>
    /// The name of the MCP tool that was executed.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// The arguments passed to the tool (JSON).
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// The result returned by the tool (JSON).
    /// </summary>
    public string? Result { get; set; }

    /// <summary>
    /// For write tools: whether the user confirmed the action.
    /// </summary>
    public bool WasConfirmed { get; set; }

    /// <summary>
    /// Whether the tool execution succeeded.
    /// </summary>
    public bool? Success { get; set; }

    /// <summary>
    /// Execution duration in milliseconds.
    /// </summary>
    public int? DurationMs { get; set; }

    public DateTime ExecutedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public AiConversationMessage Message { get; set; } = null!;
    public AiActionFeedback? Feedback { get; set; }
}
