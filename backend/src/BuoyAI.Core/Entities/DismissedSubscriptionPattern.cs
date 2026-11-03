namespace BuoyAI.Core.Entities;

/// <summary>
/// Tracks subscription patterns that the user has dismissed so we don't re-suggest them
/// </summary>
public class DismissedSubscriptionPattern
{
    public int Id { get; set; }
    public int UserId { get; set; }
    
    /// <summary>
    /// Normalized merchant name (lowercase, trimmed)
    /// </summary>
    public string MerchantName { get; set; } = string.Empty;
    
    /// <summary>
    /// Optional: specific amount range that was dismissed (null = any amount from this merchant)
    /// </summary>
    public decimal? AmountMin { get; set; }
    public decimal? AmountMax { get; set; }
    
    /// <summary>
    /// When the pattern was dismissed
    /// </summary>
    public DateTime DismissedAt { get; set; }
    
    /// <summary>
    /// Optional reason provided by user
    /// </summary>
    public string? DismissReason { get; set; }
    
    /// <summary>
    /// If true, this pattern is permanently dismissed and ignores the 60-day skip period.
    /// </summary>
    public bool IsPermanent { get; set; }
    
    // Navigation
    public User User { get; set; } = null!;
}
