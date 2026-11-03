using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

public class AIInsight
{
    public int Id { get; set; }

    [Required]
    public int UserId { get; set; }

    public int? FamilyId { get; set; } // Foreign key to Family
    
    [Required]
    [MaxLength(50)]
    public string Context { get; set; } = string.Empty; // 'dashboard', 'budget', 'savings', etc.
    
    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = string.Empty; // 'spending', 'budget', 'savings', 'prediction', 'recommendation'
    
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(1000)]
    public string Message { get; set; } = string.Empty;
    
    [MaxLength(500)]
    public string? ActionSuggestion { get; set; }
    
    public string? Data { get; set; } // JSON for any additional data
    
    public double Confidence { get; set; }
    
    [Required]
    [MaxLength(20)]
    public string Priority { get; set; } = "medium"; // 'low', 'medium', 'high'
    
    public bool IsDismissed { get; set; } = false;
    
    public bool IsArchived { get; set; } = false; // For refresh functionality
    
    public DateTime DismissedAt { get; set; }
    
    public DateTime GeneratedAt { get; set; }
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }

    // Navigation property
    public User User { get; set; } = null!;
    public Family? Family { get; set; }
}