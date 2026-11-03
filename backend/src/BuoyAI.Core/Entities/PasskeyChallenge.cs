using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Temporary storage for WebAuthn challenges during registration/authentication
/// </summary>
public class PasskeyChallenge
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Challenge { get; set; } = string.Empty;
    public string Options { get; set; } = string.Empty; // JSON of challenge options
    public string Type { get; set; } = string.Empty; // "registration" or "authentication"
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>A concurrency token: two redemptions of one challenge can't both save.</summary>
    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
    public bool IsUsed { get; set; }
    
    // Navigation properties
    public User? User { get; set; }
}
