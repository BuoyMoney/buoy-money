using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class FamilyMember
{
    public int Id { get; set; }
    public int FamilyId { get; set; }
    public int UserId { get; set; }
    public FamilyMemberRole Role { get; set; } = FamilyMemberRole.Member;
    public DateTime JoinedAt { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation properties
    public Family Family { get; set; } = null!;
    public User User { get; set; } = null!;
}
