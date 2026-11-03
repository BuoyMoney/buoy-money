using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// User feedback on specific AI tool executions.
/// Used for model training and quality improvement.
/// </summary>
public class AiActionFeedback
{
    public int Id { get; set; }

    /// <summary>
    /// The tool execution this feedback relates to.
    /// </summary>
    [Required]
    public int ToolExecutionId { get; set; }

    /// <summary>
    /// Denormalized user ID for RLS policy enforcement.
    /// </summary>
    [Required]
    public int UserId { get; set; }

    /// <summary>
    /// User's thumbs up/down rating.
    /// null = no feedback, true = positive, false = negative
    /// </summary>
    public bool? ThumbsUp { get; set; }

    /// <summary>
    /// Optional user comment explaining the feedback.
    /// </summary>
    [MaxLength(1000)]
    public string? Comment { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    // Navigation properties
    public AiToolExecution ToolExecution { get; set; } = null!;
}
