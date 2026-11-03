using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs;

// Family DTOs
public class FamilyDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public int CreatedByUserId { get; set; }
  public string CreatedByUserName { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; }
  public SubscriptionTier SubscriptionTier { get; set; }
  public DateTime? TrialStartDate { get; set; }
  public DateTime? TrialEndDate { get; set; }
  public bool IsTrialActive { get; set; }
  public int MemberCount { get; set; }
}

public class FamilyMemberDto
{
  public int FamilyId { get; set; }
  public string FamilyName { get; set; } = string.Empty;
  public int UserId { get; set; }
  public string UserName { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public FamilyMemberRole Role { get; set; }
  public string RoleName { get; set; } = string.Empty;
  public DateTime JoinedAt { get; set; }
  public bool IsActive { get; set; }
  public bool IsCurrentUser { get; set; }
}

public class FamilyInvitationDto
{
  public int Id { get; set; }
  public int FamilyId { get; set; }
  public string FamilyName { get; set; } = string.Empty;
  public string InvitedEmail { get; set; } = string.Empty;
  public string InvitationToken { get; set; } = string.Empty;
  public FamilyMemberRole InvitedRole { get; set; }
  public string InvitedRoleName { get; set; } = string.Empty;
  public int InvitedByUserId { get; set; }
  public string InvitedByUserName { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; }
  public DateTime ExpiresAt { get; set; }
  public bool IsExpired { get; set; }
  public bool IsAccepted { get; set; }
  public DateTime? AcceptedAt { get; set; }
}

// Request DTOs
public class CreateFamilyRequest
{
  public string Name { get; set; } = string.Empty;
}

public class UpdateFamilyRequest
{
  public string Name { get; set; } = string.Empty;
}

public class InviteFamilyMemberRequest
{
  public string Email { get; set; } = string.Empty;
  public FamilyMemberRole Role { get; set; }
}

public class UpdateMemberRoleRequest
{
  public int TargetUserId { get; set; }
  public FamilyMemberRole NewRole { get; set; }
}

public class AcceptInvitationRequest
{
  public string Token { get; set; } = string.Empty;
}
