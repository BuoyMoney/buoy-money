namespace BuoyAI.Core.Entities;

public class UserConsent
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    
    public string ConsentType { get; set; } = null!; // "plaid_connection", "data_sharing", etc.
    public string ConsentVersion { get; set; } = null!; // Track consent versions
    public bool HasConsented { get; set; }
    public DateTime ConsentedAt { get; set; }
    public string? ConsentText { get; set; } // Store the actual consent text shown
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
