using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Stores AI-generated visualizations for a user.
/// Visualizations are generated via chat-based AI that uses MCP tools to gather context.
/// </summary>
public class UserVisualization
{
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    public int? FamilyId { get; set; }

    /// <summary>
    /// Type of visualization: pie, bar, line, area, gauge, progress
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string ChartType { get; set; } = string.Empty;

    /// <summary>
    /// Title of the visualization
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// AI-generated message explaining the insight
    /// </summary>
    [Required]
    [MaxLength(2000)]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Motivational or contextual message based on user's quiz profile
    /// </summary>
    [MaxLength(500)]
    public string? MotivationalMessage { get; set; }

    /// <summary>
    /// JSON-serialized chart data points
    /// </summary>
    [Required]
    public string ChartData { get; set; } = "[]";

    /// <summary>
    /// JSON-serialized chart configuration (axis labels, colors, etc.)
    /// </summary>
    public string? ChartConfig { get; set; }

    /// <summary>
    /// Category of insight: spending, savings, budget, income, trends, goals
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Priority for display ordering: low, medium, high
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Priority { get; set; } = "medium";

    /// <summary>
    /// Confidence score from AI (0-1)
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// User has starred this visualization to see it again
    /// </summary>
    public bool IsStarred { get; set; } = false;

    /// <summary>
    /// User has dismissed this visualization
    /// </summary>
    public bool IsDismissed { get; set; } = false;

    /// <summary>
    /// Number of times this visualization has been viewed
    /// </summary>
    public int ViewCount { get; set; } = 0;

    /// <summary>
    /// When the visualization was last viewed
    /// </summary>
    public DateTime? LastViewedAt { get; set; }

    /// <summary>
    /// When the AI generated this visualization
    /// </summary>
    public DateTime GeneratedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public User User { get; set; } = null!;
    public Family? Family { get; set; }
}
