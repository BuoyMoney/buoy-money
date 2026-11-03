using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a single message in an AI conversation.
/// </summary>
public class AiConversationMessage
{
    public int Id { get; set; }

    [Required]
    public int ConversationId { get; set; }

    /// <summary>
    /// The role of the message sender.
    /// Values: "user", "assistant", "system"
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// The message content.
    /// </summary>
    [Required]
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Optional token count for usage tracking.
    /// </summary>
    public int? TokenCount { get; set; }

    /// <summary>
    /// The AI model used to generate this response (for assistant messages).
    /// </summary>
    [MaxLength(100)]
    public string? Model { get; set; }

    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public AiConversation Conversation { get; set; } = null!;
    public ICollection<AiToolExecution> ToolExecutions { get; set; } = new List<AiToolExecution>();
}
