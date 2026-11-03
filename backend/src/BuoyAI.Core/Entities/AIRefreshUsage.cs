using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.Entities;

public class AIRefreshUsage
{
    public int Id { get; set; }
    
    [Required]
    public int UserId { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Context { get; set; } = string.Empty; // 'dashboard', 'budget', 'savings', etc.
    
    [Required]
    public DateTime Date { get; set; } // Date without time for daily tracking
    
    public int RefreshCount { get; set; } = 0;
    
    public DateTime LastRefreshAt { get; set; }
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }
    
    // Navigation property
    public User User { get; set; } = null!;
}
