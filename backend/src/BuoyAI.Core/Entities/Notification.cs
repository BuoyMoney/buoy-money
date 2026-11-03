using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Entities;

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    
    public string Type { get; set; } = null!; // "plaid_update", "account_alert", etc.
    public string Title { get; set; } = null!;
    public string Message { get; set; } = null!;
    public string? ActionUrl { get; set; } // Optional link/action
    public string? ActionText { get; set; } // "Update Connection", "View Details", etc.
    
    // Metadata for specific notification types
    public int? RelatedConnectionId { get; set; } // For Plaid notifications
    public PlaidConnection? RelatedConnection { get; set; }
    
    public bool IsRead { get; set; } = false;
    public DateTime? ReadAt { get; set; }
    public DateTime? ArchivedAt { get; set; } // For notifications older than 1 month
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
