using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents an AI conversation session for a user.
/// Conversations are strictly isolated by user for privacy.
/// </summary>
public class AiConversation
{
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// Unique session identifier for grouping messages.
    /// This is passed from the frontend to maintain conversation context.
    /// </summary>
    [Required]
    public Guid SessionId { get; set; }

    /// <summary>
    /// The context in which the conversation was started.
    /// Values: "dashboard", "budget", "savings", "chat", "transactions"
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Context { get; set; } = "chat";

    /// <summary>
    /// Optional title for the conversation, can be auto-generated from first message.
    /// </summary>
    [MaxLength(200)]
    public string? Title { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime LastMessageAt { get; set; }

    /// <summary>
    /// Whether this conversation is still active (not archived/deleted).
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public ICollection<AiConversationMessage> Messages { get; set; } = new List<AiConversationMessage>();
}
