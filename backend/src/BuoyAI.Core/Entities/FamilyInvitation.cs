using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class FamilyInvitation
{
    public int Id { get; set; }
    public int FamilyId { get; set; }
    public string InvitedEmail { get; set; } = string.Empty;
    public int InvitedByUserId { get; set; }
    public FamilyMemberRole InvitedRole { get; set; } = FamilyMemberRole.Member;
    public string InvitationToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public bool IsAccepted { get; set; } = false;
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public Family Family { get; set; } = null!;
    public User InvitedByUser { get; set; } = null!;
}
