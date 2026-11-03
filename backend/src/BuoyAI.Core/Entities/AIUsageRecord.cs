using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Tracks AI API usage per request for cost tracking and billing.
/// Used to enforce monthly limits (warn at 80%, hard block at 100%).
/// </summary>
public class AIUsageRecord
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = string.Empty;
    
    public int InputTokens { get; set; }
    
    public int OutputTokens { get; set; }
    
    [Column(TypeName = "decimal(10, 6)")]
    public decimal EstimatedCost { get; set; }
    
    [Required]
    public DateTime Timestamp { get; set; }
    
    /// <summary>
    /// Context of the request: 'chat', 'insights', 'categorization', etc.
    /// </summary>
    [MaxLength(50)]
    public string? RequestContext { get; set; }
    
    /// <summary>
    /// Optional conversation ID for chat requests
    /// </summary>
    public int? ConversationId { get; set; }

    /// <summary>Which provider answered: "gemini", "ollama", "openrouter", "buoy-cloud" and so on. Null on rows written before this was recorded.</summary>
    [MaxLength(30)]
    public string? Provider { get; set; }

    /// <summary>
    /// True when the token counts are a guess (a character count, or one total split 70/30), false when the provider
    /// reported them. Every row written before migration 0053 is a guess, which is the column default.
    /// </summary>
    public bool TokensEstimated { get; set; } = true;
    
    public DateTime CreatedAt { get; set; }
    
    // Navigation property
    public User User { get; set; } = null!;
}
