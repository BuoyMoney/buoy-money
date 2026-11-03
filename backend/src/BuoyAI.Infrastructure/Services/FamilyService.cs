using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using BuoyAI.Infrastructure.Utilities;

namespace BuoyAI.Infrastructure.Services;

public class FamilyService : IFamilyService
{
  private readonly IFamilyRepository _familyRepository;
  private readonly IFamilyMemberRepository _familyMemberRepository;
  private readonly IFamilyInvitationRepository _familyInvitationRepository;
  private readonly IUserRepository _userRepository;
  private readonly IBudgetRepository _budgetRepository;
  private readonly ISavingsRepository _savingsRepository;
  private readonly IAccountRepository _accountRepository;
  private readonly IPlaidConnectionRepository _plaidConnectionRepository;
  private readonly IEmailService _emailService;
  private readonly INotificationService _notificationService;
  private readonly IConfiguration _configuration;
  private readonly ILogger<FamilyService> _logger;
  private readonly Telemetry.BuoyAiMetrics _metrics;

  private readonly IEntitlementService _entitlements;

  public FamilyService(
    IFamilyRepository familyRepository,
    IFamilyMemberRepository familyMemberRepository,
    IFamilyInvitationRepository familyInvitationRepository,
    IUserRepository userRepository,
    IBudgetRepository budgetRepository,
    ISavingsRepository savingsRepository,
    IAccountRepository accountRepository,
    IPlaidConnectionRepository plaidConnectionRepository,
    IEmailService emailService,
    INotificationService notificationService,
    IConfiguration configuration,
    IEntitlementService entitlements,
    ILogger<FamilyService> logger,
    Telemetry.BuoyAiMetrics metrics)
  {
    _entitlements = entitlements;
    _familyRepository = familyRepository;
    _familyMemberRepository = familyMemberRepository;
    _familyInvitationRepository = familyInvitationRepository;
    _userRepository = userRepository;
    _budgetRepository = budgetRepository;
    _savingsRepository = savingsRepository;
    _accountRepository = accountRepository;
    _plaidConnectionRepository = plaidConnectionRepository;
    _emailService = emailService;
    _notificationService = notificationService;
    _configuration = configuration;
    _logger = logger;
    _metrics = metrics;
  }

  #region Family Management

  public async Task<FamilyDto> CreateFamilyAsync(int userId, CreateFamilyRequest request)
  {
    _logger.LogInformation("User {UserId} creating family: {FamilyName}", userId, request.Name);

    // Check premium subscription requirement and get the user (avoiding duplicate fetch)
    var user = await EnsurePremiumSubscriptionAsync(userId);

    if (user.FamilyId.HasValue)
      throw new InvalidOperationException("User is already in a family. Leave current family first.");

    // Create the family
    var family = new Family
    {
      Name = request.Name,
      CreatedByUserId = userId,
      CreatedAt = DateTime.UtcNow
    };

    await _familyRepository.CreateAsync(family);

    // Add creator as Owner
    var familyMember = new FamilyMember
    {
      FamilyId = family.Id,
      UserId = userId,
      Role = FamilyMemberRole.Owner,
      JoinedAt = DateTime.UtcNow,
      IsActive = true
    };

    await _familyMemberRepository.CreateAsync(familyMember);

    // Update user's FamilyId
    user.FamilyId = family.Id;
    await _userRepository.UpdateAsync(user);

    // Migrate the owner's existing data to the new family
    await MigrateUserDataToFamilyAsync(userId, family.Id);

    // Creating an own family implicitly declines any pending invitations addressed
    // to this user — remove them and let each inviter know.
    await DeclinePendingInvitationsForUserAsync(user, "created their own family");

    _logger.LogInformation("Family {FamilyId} created by user {UserId}", family.Id, userId);

    return await MapToFamilyDto(family);
  }

  /// <summary>
  /// Removes every pending invitation addressed to the given user and notifies each
  /// inviter that the invitation was declined. Failures to notify never abort the caller.
  /// </summary>
  private async Task DeclinePendingInvitationsForUserAsync(User user, string reason)
  {
    var pendingInvitations = await _familyInvitationRepository.GetByEmailAsync(user.Email);
    if (pendingInvitations == null)
      return;

    foreach (var invitation in pendingInvitations)
    {
      // Best-effort cleanup: a failure here must not abort the caller (the family
      // has already been created), so swallow and log per-invitation errors.
      try
      {
        await _familyInvitationRepository.DeleteAsync(invitation.Id);

        await _notificationService.CreateNotificationAsync(invitation.InvitedByUserId, new CreateNotificationRequest
        {
          Type = "family_invitation_declined",
          Title = "Invitation Declined",
          Message = $"{user.FirstName} {user.LastName} declined your family invitation because they {reason}.",
        });

        _logger.LogInformation("Pending invitation {InvitationId} auto-declined for user {UserId} ({Reason})", invitation.Id, user.Id, reason);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to auto-decline pending invitation {InvitationId} for user {UserId}", invitation.Id, user.Id);
      }
    }
  }

  public async Task<FamilyDto?> GetFamilyAsync(int userId)
  {
    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      return null;

    var family = await _familyRepository.GetByIdAsync(user.FamilyId.Value);
    if (family == null)
      return null;

    return await MapToFamilyDto(family);
  }

  public async Task<FamilyDto?> GetFamilyByIdAsync(int familyId)
  {
    var family = await _familyRepository.GetByIdAsync(familyId);
    if (family == null)
      return null;

    return await MapToFamilyDto(family);
  }

  public async Task<FamilyDto> UpdateFamilyAsync(int userId, UpdateFamilyRequest request)
  {
    _logger.LogInformation("User {UserId} updating family name to: {FamilyName}", userId, request.Name);

    // Check permission
    if (!await CanUserManageFamilyAsync(userId))
      throw new UnauthorizedAccessException("Only family owners can update family settings");

    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    var family = await _familyRepository.GetByIdAsync(user.FamilyId.Value);
    if (family == null)
      throw new InvalidOperationException("Family not found");

    family.Name = request.Name;
    await _familyRepository.UpdateAsync(family);

    _logger.LogInformation("Family {FamilyId} updated by user {UserId}", family.Id, userId);

    return await MapToFamilyDto(family);
  }

  public async Task DeleteFamilyAsync(int userId)
  {
    _logger.LogInformation("User {UserId} attempting to delete family", userId);

    // Check permission - only owner can delete
    if (!await CanUserManageFamilyAsync(userId))
      throw new UnauthorizedAccessException("Only family owners can delete the family");

    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    var role = await GetUserRoleInFamilyAsync(userId);
    if (role != FamilyMemberRole.Owner)
      throw new UnauthorizedAccessException("Only the family owner can delete the family");

    var familyId = user.FamilyId.Value;

    // Members' Premium came from this family. PremiumInherited is gated purely on the stored
    // tier, so without this reset every member would keep Premium after the family is gone
    // (the same reset LeaveFamilyAsync and RemoveMemberAsync do for one person).
    var members = await _familyRepository.GetMembersAsync(familyId);
    foreach (var member in members)
    {
      if (member.User is { SubscriptionTier: SubscriptionTier.PremiumInherited } memberUser)
      {
        memberUser.SubscriptionTier = null;
        await _userRepository.UpdateAsync(memberUser);
        _logger.LogInformation("Reset PremiumInherited tier for user {UserId} on family delete", memberUser.Id);
      }
    }

    // Members and invitations cascade; budgets, goals, accounts, bank connections,
    // subscriptions and insights are kept and detached (ON DELETE SET NULL, migration 0041).
    await _familyRepository.DeleteAsync(familyId);

    _logger.LogInformation("Family {FamilyId} deleted by owner {UserId}", familyId, userId);
  }

  public async Task LeaveFamilyAsync(int userId)
  {
    _logger.LogInformation("User {UserId} leaving family", userId);

    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    var role = await GetUserRoleInFamilyAsync(userId);
    if (role == FamilyMemberRole.Owner)
      throw new InvalidOperationException("The family owner can't leave. Delete the family instead, or email contact@buoy-ai.com to hand ownership to another member.");

    // Store family ID for email notification
    var familyId = user.FamilyId.Value;
    var family = await _familyRepository.GetByIdAsync(familyId);

    // Remove from family
    await _familyMemberRepository.RemoveMemberAsync(user.FamilyId.Value, userId);

    // Update user's FamilyId to null and reset inherited subscription tier
    user.FamilyId = null;
    if (user.SubscriptionTier == SubscriptionTier.PremiumInherited)
    {
      user.SubscriptionTier = null;
      _logger.LogInformation("Reset PremiumInherited tier for user {UserId} who left family", userId);
    }
    await _userRepository.UpdateAsync(user);

    _logger.LogInformation("User {UserId} left family {FamilyId}", userId, familyId);

    // Send notification email to family owner
    try
    {
      if (family != null)
      {
        var owner = await _userRepository.GetByIdAsync(family.CreatedByUserId);
        if (owner != null)
        {
          await _emailService.SendFamilyMemberLeftAsync(
            owner.Email,
            owner.FirstName,
            $"{user.FirstName} {user.LastName}",
            user.Email
          );

          _logger.LogInformation("Member left notification sent to owner {Email}", owner.Email);
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send member left email");
      // Don't fail the leave action if email fails
    }
  }

  #endregion

  #region Member Management

  public async Task<IEnumerable<FamilyMemberDto>> GetFamilyMembersAsync(int userId)
  {
    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    var members = await _familyMemberRepository.GetByFamilyIdAsync(user.FamilyId.Value);
    
    var memberDtos = new List<FamilyMemberDto>();
    foreach (var member in members)
    {
      var memberUser = await _userRepository.GetByIdAsync(member.UserId);
      if (memberUser != null)
      {
        memberDtos.Add(MapToFamilyMemberDto(member, memberUser, userId));
      }
    }

    return memberDtos;
  }

  public async Task<FamilyMemberDto> UpdateMemberRoleAsync(int userId, int targetUserId, FamilyMemberRole newRole)
  {
    _logger.LogInformation("User {UserId} updating role of user {TargetUserId} to {NewRole}", 
      userId, targetUserId, newRole);

    // Check permission - only owner can change roles
    if (!await CanUserManageFamilyAsync(userId))
      throw new UnauthorizedAccessException("Only family owners can change member roles");

    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    // Can't change the owner's role
    var targetMember = await _familyMemberRepository.GetByUserIdAsync(targetUserId);
    if (targetMember == null || targetMember.FamilyId != user.FamilyId)
      throw new InvalidOperationException("Target user is not in your family");

    if (targetMember.Role == FamilyMemberRole.Owner)
      throw new InvalidOperationException("Cannot change the owner's role");

    // Never mint a second Owner via a role update. A family has exactly one
    // Owner (set at creation / transferred explicitly). Defence-in-depth: even
    // if a client sends a bad payload that binds NewRole to 0 (Owner), reject it
    // rather than granting the target full billing + family control.
    if (newRole == FamilyMemberRole.Owner)
      throw new InvalidOperationException("Cannot promote a member to Owner");

    // Store old role for email notification
    var oldRole = targetMember.Role;

    // Update role
    targetMember.Role = newRole;
    await _familyMemberRepository.UpdateAsync(targetMember);

    var targetUser = await _userRepository.GetByIdAsync(targetUserId);
    
    _logger.LogInformation("User {TargetUserId} role updated to {NewRole} in family {FamilyId}", 
      targetUserId, newRole, user.FamilyId);

    // Send notification email to target user
    try
    {
      if (targetUser != null)
      {
        var oldRoleName = GetRoleName(oldRole);
        var newRoleName = GetRoleName(newRole);
        await _emailService.SendFamilyRoleChangedAsync(
          targetUser.Email,
          targetUser.FirstName,
          oldRoleName,
          newRoleName,
          $"{user.FirstName} {user.LastName}"
        );

        _logger.LogInformation("Role changed notification sent to {Email}", targetUser.Email);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send role changed email to {UserId}", targetUserId);
      // Don't fail the role change if email fails
    }

    return MapToFamilyMemberDto(targetMember, targetUser!);
  }

  public async Task RemoveMemberAsync(int userId, int targetUserId)
  {
    _logger.LogInformation("User {UserId} removing user {TargetUserId} from family", userId, targetUserId);

    // Check permission - only owner can remove members
    if (!await CanUserManageFamilyAsync(userId))
      throw new UnauthorizedAccessException("Only family owners can remove members");

    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    // Can't remove the owner
    var targetMember = await _familyMemberRepository.GetByUserIdAsync(targetUserId);
    if (targetMember == null || targetMember.FamilyId != user.FamilyId)
      throw new InvalidOperationException("Target user is not in your family");

    if (targetMember.Role == FamilyMemberRole.Owner)
      throw new InvalidOperationException("Cannot remove the family owner");

    // Remove member
    await _familyMemberRepository.RemoveMemberAsync(user.FamilyId.Value, targetUserId);

    // Update target user's FamilyId and reset inherited subscription tier
    var targetUser = await _userRepository.GetByIdAsync(targetUserId);
    if (targetUser != null)
    {
      targetUser.FamilyId = null;
      // Reset inherited premium tier when leaving family
      if (targetUser.SubscriptionTier == SubscriptionTier.PremiumInherited)
      {
        targetUser.SubscriptionTier = null;
        _logger.LogInformation("Reset PremiumInherited tier for removed user {UserId}", targetUserId);
      }
      await _userRepository.UpdateAsync(targetUser);
    }

    _logger.LogInformation("User {TargetUserId} removed from family {FamilyId}", targetUserId, user.FamilyId);

    // Send notification email to removed member
    try
    {
      if (targetUser != null)
      {
        var family = await _familyRepository.GetByIdAsync(user.FamilyId.Value);
        var familyName = family?.Name ?? "your family";
        await _emailService.SendFamilyMemberRemovedAsync(
          targetUser.Email,
          targetUser.FirstName,
          familyName,
          $"{user.FirstName} {user.LastName}"
        );

        _logger.LogInformation("Member removed notification sent to {Email}", targetUser.Email);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send member removed email to {UserId}", targetUserId);
      // Don't fail the removal if email fails
    }
  }

  #endregion

  #region Invitation Management

  public async Task<FamilyInvitationDto> InviteMemberAsync(int userId, InviteFamilyMemberRequest request)
  {
    _logger.LogInformation("User {UserId} inviting {Email} to family with role {Role}", 
      userId, request.Email, request.Role);

    // Check premium subscription requirement and get the user (avoiding duplicate fetch)
    var user = await EnsurePremiumSubscriptionAsync(userId);

    // Check permission - only owner can invite (or user without family, who will become owner)
    var hasFamily = user.FamilyId.HasValue;
    if (hasFamily && !await CanUserManageFamilyAsync(userId))
      throw new UnauthorizedAccessException("Only family owners can invite members");

    // Auto-create family if user doesn't have one
    if (!hasFamily)
    {
      _logger.LogInformation("User {UserId} doesn't have a family, auto-creating one", userId);
      
      // Create a family with user's last name or a default name
      var familyName = !string.IsNullOrEmpty(user.LastName) 
        ? $"{user.LastName} Family" 
        : $"{user.FirstName}'s Family";
      
      var createRequest = new CreateFamilyRequest { Name = familyName };
      await CreateFamilyAsync(userId, createRequest);
      
      // Reload user to get updated FamilyId
      user = await _userRepository.GetByIdAsync(userId) ?? throw new InvalidOperationException("User not found after family creation");
      
      _logger.LogInformation("Auto-created family for user {UserId} with name {FamilyName}", userId, familyName);
    }

    // At this point, user.FamilyId is guaranteed to be set
    var familyId = user.FamilyId!.Value;

    // Store the address the way accounts are stored (trimmed, lowercased) so the invitee's
    // email lookups (their in-app invite list, the sign-in banner) find it.
    var invitedEmail = request.Email.Trim().ToLowerInvariant();

    // Check if email is already in the family
    var existingUser = await _userRepository.GetByEmailAsync(invitedEmail);
    if (existingUser?.FamilyId == familyId)
      throw new InvalidOperationException("User is already in your family");

    // Check if there's already a pending invitation
    var pendingInvitations = await _familyInvitationRepository.GetByFamilyIdAsync(familyId);
    var existingInvitation = pendingInvitations.FirstOrDefault(i => 
      i.InvitedEmail.ToLower() == invitedEmail && 
      !i.IsAccepted && 
      i.ExpiresAt > DateTime.UtcNow);

    if (existingInvitation != null)
      throw new InvalidOperationException("There is already a pending invitation for this email");

    // Can't invite someone as Owner
    if (request.Role == FamilyMemberRole.Owner)
      throw new InvalidOperationException("Cannot invite someone as Owner");

    // Create invitation
    var invitation = new FamilyInvitation
    {
      FamilyId = familyId,
      InvitedEmail = invitedEmail,
      InvitationToken = GenerateInvitationToken(),
      InvitedRole = request.Role,
      InvitedByUserId = userId,
      CreatedAt = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.AddDays(7), // 7-day expiration
      IsAccepted = false
    };

    await _familyInvitationRepository.CreateAsync(invitation);

    _logger.LogInformation("Invitation {InvitationId} created for {Email} to family {FamilyId}", 
      invitation.Id, request.Email, familyId);

    // Send invitation email
    try
    {
      var family = await _familyRepository.GetByIdAsync(familyId);
      var familyName = family?.Name ?? "your family";
      var invitationLink = $"{GetBaseUrl()}/app/accept-invitation/{invitation.InvitationToken}";
      var roleName = GetRoleName(request.Role);
      
      await _emailService.SendFamilyInvitationAsync(
        request.Email,
        $"{user.FirstName} {user.LastName}",
        familyName,
        roleName,
        invitationLink
      );

      _logger.LogInformation("Invitation email sent to {Email}", request.Email);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send invitation email to {Email}", request.Email);
    }

    // Create in-app notification if invitee already has an account
    if (existingUser != null && !existingUser.IsDeleted)
    {
      try
      {
        var family = await _familyRepository.GetByIdAsync(familyId);
        var familyName = family?.Name ?? "your family";
        await _notificationService.CreateNotificationAsync(existingUser.Id, new CreateNotificationRequest
        {
          Type = "family_invitation",
          Title = "Family Invitation",
          Message = $"{user.FirstName} {user.LastName} invited you to join {familyName} as {GetRoleName(request.Role)}.",
          ActionUrl = $"/accept-invitation/{invitation.InvitationToken}",
          ActionText = "View Invitation"
        });
        _logger.LogInformation("Created in-app notification for existing user {UserId} for family invitation", existingUser.Id);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to create notification for invitation to {Email}", request.Email);
      }
    }

    _metrics.RecordFamilyInviteSent(); // Business dashboard: family-invites-sent rate

    return await MapToFamilyInvitationDto(invitation);
  }

  public async Task<IEnumerable<FamilyInvitationDto>> GetPendingInvitationsAsync(int userId)
  {
    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    var invitations = await _familyInvitationRepository.GetByFamilyIdAsync(user.FamilyId.Value);
    var pending = invitations.Where(i => !i.IsAccepted && i.ExpiresAt > DateTime.UtcNow);

    var dtos = new List<FamilyInvitationDto>();
    foreach (var invitation in pending)
    {
      dtos.Add(await MapToFamilyInvitationDto(invitation));
    }

    return dtos;
  }

  public async Task<FamilyInvitationDto?> GetInvitationByTokenAsync(string token)
  {
    // Any state: signing up from an invite link accepts it and THEN opens the invitation page,
    // which must see "accepted" (the DTO carries IsAccepted / IsExpired), not "not found".
    var invitation = await _familyInvitationRepository.GetByTokenAnyStateAsync(token);
    if (invitation == null)
      return null;

    return await MapToFamilyInvitationDto(invitation);
  }

  public async Task<IEnumerable<FamilyInvitationDto>> GetPendingInvitationsForUserAsync(int userId)
  {
    var user = await _userRepository.GetByIdAsync(userId)
      ?? throw new InvalidOperationException("User not found");

    var invitations = await _familyInvitationRepository.GetByEmailAsync(user.Email);
    var dtos = new List<FamilyInvitationDto>();
    foreach (var invitation in invitations)
    {
      dtos.Add(await MapToFamilyInvitationDto(invitation));
    }
    return dtos;
  }

  /// <summary>
  /// Migrates a user's existing data (budgets, savings, accounts, connections) to their family.
  /// This ensures all family members see shared data.
  /// </summary>
  private async Task MigrateUserDataToFamilyAsync(int userId, int familyId)
  {
    _logger.LogInformation("Migrating user {UserId} data to family {FamilyId}", userId, familyId);

    try
    {
      var budgets = await _budgetRepository.GetByUserIdAsync(userId);
      var userPersonalBudgets = budgets.Where(b => !b.FamilyId.HasValue && b.Type == BudgetType.Personal).ToList();

      // Check if the family already has an active family budget (from the owner)
      var existingFamilyBudgets = await _budgetRepository.GetByFamilyIdAsync(familyId);
      var hasActiveFamilyBudget = existingFamilyBudgets.Any(b => b.Type == BudgetType.Family && b.IsActive);

      if (hasActiveFamilyBudget && userPersonalBudgets.Any())
      {
        // Family already has a budget — link the invitee's personal budgets as LIVE children of
        // it (don't deactivate them), so every member's budget stays visible under the family.
        var familyBudget = existingFamilyBudgets.First(b => b.Type == BudgetType.Family && b.IsActive);
        foreach (var budget in userPersonalBudgets)
        {
          budget.FamilyId = familyId;
          budget.ParentBudgetId = familyBudget.Id;
          await _budgetRepository.UpdateAsync(budget);
        }
        _logger.LogInformation("Linked {Count} personal budgets as children of family budget {FamilyBudgetId} for user {UserId}", userPersonalBudgets.Count, familyBudget.Id, userId);
      }
      else
      {
        // No family budget yet — convert user's personal budgets to family budgets
        foreach (var budget in userPersonalBudgets)
        {
          budget.FamilyId = familyId;
          budget.Type = BudgetType.Family;
          await _budgetRepository.UpdateAsync(budget);
        }
        _logger.LogInformation("Converted {Count} personal budgets to family budgets for user {UserId}", userPersonalBudgets.Count, userId);
      }

      // Migrate any other non-personal budgets (allowances, etc.)
      foreach (var budget in budgets.Where(b => !b.FamilyId.HasValue && b.Type != BudgetType.Personal))
      {
        budget.FamilyId = familyId;
        await _budgetRepository.UpdateAsync(budget);
      }

      // Migrate savings goals
      var savingsGoals = await _savingsRepository.GetByUserIdAsync(userId);
      foreach (var goal in savingsGoals.Where(g => !g.FamilyId.HasValue))
      {
        goal.FamilyId = familyId;
        await _savingsRepository.UpdateAsync(goal);
      }
      _logger.LogInformation("Migrated {Count} savings goals for user {UserId}", savingsGoals.Count(g => !g.FamilyId.HasValue), userId);

      // Migrate accounts
      var accounts = await _accountRepository.GetByUserIdAsync(userId);
      foreach (var account in accounts.Where(a => !a.FamilyId.HasValue))
      {
        account.FamilyId = familyId;
        await _accountRepository.UpdateAsync(account);
      }
      _logger.LogInformation("Migrated {Count} accounts for user {UserId}", accounts.Count(a => !a.FamilyId.HasValue), userId);

      // Migrate Plaid connections
      var connections = await _plaidConnectionRepository.GetByUserIdAsync(userId);
      foreach (var connection in connections.Where(c => !c.FamilyId.HasValue))
      {
        connection.FamilyId = familyId;
        await _plaidConnectionRepository.UpdateAsync(connection);
      }
      _logger.LogInformation("Migrated {Count} Plaid connections for user {UserId}", connections.Count(c => !c.FamilyId.HasValue), userId);

      _logger.LogInformation("Successfully migrated all data for user {UserId} to family {FamilyId}", userId, familyId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error migrating data for user {UserId} to family {FamilyId}", userId, familyId);
      throw;
    }
  }

  public async Task<FamilyMemberDto> AcceptInvitationAsync(int userId, string token)
  {
    _logger.LogInformation("User {UserId} accepting invitation with token {Token}", userId, token);

    // Any state, so the accepted/expired checks below can run. The pending-only lookup hid
    // accepted invitations, which turned the idempotent replay into "Invitation not found".
    var invitation = await _familyInvitationRepository.GetByTokenAnyStateAsync(token);
    if (invitation == null)
      throw new InvalidOperationException("Invitation not found");

    // Re-accepting is a NO-OP for the user who already accepted, not an error. The accept
    // screen can fire twice (double-click, client retry, refresh), and the second call used to
    // surface a "already been accepted" 400 on top of a join that had fully succeeded — the
    // user saw a failure toast for a completed action. Only reject when the invitation was
    // consumed by SOMEONE ELSE.
    if (invitation.IsAccepted)
    {
      var existingMember = await _familyMemberRepository.GetByUserIdAsync(userId);
      if (existingMember != null && existingMember.IsActive && existingMember.FamilyId == invitation.FamilyId)
      {
        var acceptedBy = await _userRepository.GetByIdAsync(userId);
        if (acceptedBy != null)
        {
          _logger.LogInformation(
            "Invitation {InvitationId} was already accepted by user {UserId}; returning existing membership (idempotent replay)",
            invitation.Id, userId);
          return MapToFamilyMemberDto(existingMember, acceptedBy);
        }
      }

      throw new InvalidOperationException("Invitation has already been accepted");
    }

    if (invitation.ExpiresAt < DateTime.UtcNow)
      throw new InvalidOperationException("Invitation has expired");

    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null)
      throw new InvalidOperationException("User not found");

    // Check if email matches
    if (!user.Email.Equals(invitation.InvitedEmail, StringComparison.OrdinalIgnoreCase))
      throw new UnauthorizedAccessException("This invitation is not for your email address");

    // Check if user is already in a family
    if (user.FamilyId.HasValue)
      throw new InvalidOperationException("You are already in a family. Leave your current family first.");

    // Add user to family. Leaving or being removed only deactivates the membership row, and
    // the database allows one row per person per family (IX_FamilyMembers_UserId_FamilyId),
    // so someone coming back gets their old row reactivated instead of a second insert.
    var familyMember = await _familyMemberRepository.GetByUserAndFamilyAnyStateAsync(userId, invitation.FamilyId);
    if (familyMember != null)
    {
      familyMember.Role = invitation.InvitedRole;
      familyMember.JoinedAt = DateTime.UtcNow;
      familyMember.IsActive = true;
      await _familyMemberRepository.UpdateAsync(familyMember);
      _logger.LogInformation("Reactivated membership {MemberId} for user {UserId} rejoining family {FamilyId}",
        familyMember.Id, userId, invitation.FamilyId);
    }
    else
    {
      familyMember = new FamilyMember
      {
        FamilyId = invitation.FamilyId,
        UserId = userId,
        Role = invitation.InvitedRole,
        JoinedAt = DateTime.UtcNow,
        IsActive = true
      };

      await _familyMemberRepository.CreateAsync(familyMember);
    }

    // Update user's FamilyId
    user.FamilyId = invitation.FamilyId;
    
    // If family owner has premium, grant inherited premium to the new member
    var family = await _familyRepository.GetByIdAsync(invitation.FamilyId);
    if (family != null)
    {
      var familyOwner = await _userRepository.GetByIdAsync(family.CreatedByUserId);
      // Same rule as the entitlement gate, so a family owned by an allowlisted operator
      // still passes inherited premium down to the members they invite.
      if (familyOwner != null && _entitlements.HasPremiumAccess(familyOwner))
      {
        user.SubscriptionTier = SubscriptionTier.PremiumInherited;
        _logger.LogInformation("Granted PremiumInherited tier to user {UserId} from family owner {OwnerId}", 
          userId, family.CreatedByUserId);
      }
    }
    
    await _userRepository.UpdateAsync(user);

    // Migrate user's existing data to the family
    await MigrateUserDataToFamilyAsync(userId, invitation.FamilyId);

    // Mark invitation as accepted
    invitation.IsAccepted = true;
    invitation.AcceptedAt = DateTime.UtcNow;
    await _familyInvitationRepository.UpdateAsync(invitation);

    _logger.LogInformation("User {UserId} joined family {FamilyId} with role {Role}", 
      userId, invitation.FamilyId, invitation.InvitedRole);

    // Send notification email to inviter
    try
    {
      var inviter = await _userRepository.GetByIdAsync(invitation.InvitedByUserId);
      if (inviter != null)
      {
        var roleName = GetRoleName(invitation.InvitedRole);
        await _emailService.SendFamilyInvitationAcceptedAsync(
          inviter.Email,
          inviter.FirstName,
          $"{user.FirstName} {user.LastName}",
          user.Email,
          roleName
        );

        _logger.LogInformation("Invitation accepted notification sent to {Email}", inviter.Email);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to send invitation accepted email");
      // Don't fail the acceptance if email fails
    }

    return MapToFamilyMemberDto(familyMember, user);
  }

  public async Task CancelInvitationAsync(int userId, int invitationId)
  {
    _logger.LogInformation("User {UserId} canceling invitation {InvitationId}", userId, invitationId);

    // Check permission - only owner can cancel invitations
    if (!await CanUserManageFamilyAsync(userId))
      throw new UnauthorizedAccessException("Only family owners can cancel invitations");

    var user = await _userRepository.GetByIdAsync(userId);
    if (user?.FamilyId == null)
      throw new InvalidOperationException("User is not in a family");

    var invitation = await _familyInvitationRepository.GetByIdAsync(invitationId);
    if (invitation == null || invitation.FamilyId != user.FamilyId)
      throw new InvalidOperationException("Invitation not found in your family");

    await _familyInvitationRepository.DeleteAsync(invitationId);

    _logger.LogInformation("Invitation {InvitationId} canceled by user {UserId}", invitationId, userId);
  }

  public async Task DeclineInvitationAsync(int userId, string token)
  {
    _logger.LogInformation("User {UserId} declining invitation with token {Token}", userId, token);

    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null)
      throw new InvalidOperationException("User not found");

    var invitation = await _familyInvitationRepository.GetByTokenAsync(token);
    if (invitation == null)
      throw new InvalidOperationException("Invitation not found");

    if (!string.Equals(invitation.InvitedEmail, user.Email, StringComparison.OrdinalIgnoreCase))
      throw new UnauthorizedAccessException("This invitation is not addressed to you");

    await _familyInvitationRepository.DeleteAsync(invitation.Id);

    try
    {
      await _notificationService.CreateNotificationAsync(invitation.InvitedByUserId, new CreateNotificationRequest
      {
        Type = "family_invitation_declined",
        Title = "Invitation Declined",
        Message = $"{user.FirstName} {user.LastName} declined your family invitation.",
      });
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to create decline notification for user {UserId}", invitation.InvitedByUserId);
    }

    _logger.LogInformation("Invitation {InvitationId} declined by user {UserId}", invitation.Id, userId);
  }

  #endregion

  #region Permission Checking

  public async Task<bool> CanUserModifyFinancialDataAsync(int userId)
  {
    var role = await GetUserRoleInFamilyAsync(userId);
    if (role == null)
      return true; // User not in family, can modify their own data

    // Owner, BudgetAdmin, and Member can modify financial data
    // ReadOnly cannot
    return role.Value != FamilyMemberRole.ReadOnly;
  }

  public async Task<bool> CanUserManageFamilyAsync(int userId)
  {
    var role = await GetUserRoleInFamilyAsync(userId);
    if (role == null)
      return false;

    // Only Owner can manage family
    return role.Value == FamilyMemberRole.Owner;
  }

  public async Task<bool> CanUserManageBillingAsync(int userId)
  {
    var role = await GetUserRoleInFamilyAsync(userId);
    if (role == null)
      return true; // User not in family, can manage their own billing

    // Only Owner can manage billing
    return role.Value == FamilyMemberRole.Owner;
  }

  public async Task<FamilyMemberRole?> GetUserRoleInFamilyAsync(int userId)
  {
    var member = await _familyMemberRepository.GetByUserIdAsync(userId);
    return member?.Role;
  }

  public async Task<bool> IsUserInFamilyAsync(int userId, int familyId)
  {
    return await _familyRepository.IsUserInFamilyAsync(userId, familyId);
  }

  #endregion

  #region Helper Methods

  /// <summary>
  /// Checks if user has premium-level access (paid, trial, inherited, or admin).
  /// </summary>
  private async Task<bool> HasPremiumSubscriptionAsync(int userId, User? existingUser = null)
  {
    var user = existingUser ?? await _userRepository.GetByIdAsync(userId);
    if (user == null)
      return false;

    // Delegates so the operator allowlist grants ACCESS here, not only the label the profile
    // DTO shows. Reading the column directly is what let an allowlisted operator see "Admin"
    // in their profile while this gate answered 403.
    return _entitlements.HasPremiumAccess(user);
  }

  /// <summary>
  /// Ensures user has premium subscription or throws SubscriptionRequiredException.
  /// Returns the user to avoid duplicate fetches.
  /// </summary>
  private async Task<User> EnsurePremiumSubscriptionAsync(int userId)
  {
    var user = await _userRepository.GetByIdAsync(userId);
    if (user == null)
    {
      throw new InvalidOperationException("User not found");
    }

    if (!await HasPremiumSubscriptionAsync(userId, user))
    {
      _logger.LogWarning("User {UserId} attempted to access family features without premium subscription", userId);
      throw new SubscriptionRequiredException("Family features");
    }

    return user;
  }

  private async Task<FamilyDto> MapToFamilyDto(Family family)
  {
    var members = await _familyMemberRepository.GetByFamilyIdAsync(family.Id);
    var creator = await _userRepository.GetByIdAsync(family.CreatedByUserId);

    // Get subscription info from creator/owner (subscription is at user level, not family)
    return new FamilyDto
    {
      Id = family.Id,
      Name = family.Name,
      CreatedByUserId = family.CreatedByUserId,
      CreatedByUserName = creator?.Username ?? "Unknown",
      CreatedAt = family.CreatedAt,
      SubscriptionTier = creator?.SubscriptionTier ?? SubscriptionTier.Standard,
      TrialStartDate = creator?.TrialStartDate,
      TrialEndDate = creator?.TrialStartDate?.AddDays(14),
      IsTrialActive = creator?.TrialStartDate.HasValue == true && 
                     creator.TrialStartDate!.Value.AddDays(14) > DateTime.UtcNow,
      MemberCount = members.Count()
    };
  }

  private FamilyMemberDto MapToFamilyMemberDto(FamilyMember member, User user, int? currentUserId = null)
  {
    return new FamilyMemberDto
    {
      FamilyId = member.FamilyId,
      FamilyName = user.Family?.Name ?? "Unknown",
      UserId = member.UserId,
      UserName = user.Username,
      Email = user.Email,
      FirstName = user.FirstName,
      LastName = user.LastName,
      Role = member.Role,
      RoleName = member.Role.ToString(),
      JoinedAt = member.JoinedAt,
      IsActive = member.IsActive,
      IsCurrentUser = currentUserId.HasValue && member.UserId == currentUserId.Value
    };
  }

  private async Task<FamilyInvitationDto> MapToFamilyInvitationDto(FamilyInvitation invitation)
  {
    var family = await _familyRepository.GetByIdAsync(invitation.FamilyId);
    var invitedBy = await _userRepository.GetByIdAsync(invitation.InvitedByUserId);

    return new FamilyInvitationDto
    {
      Id = invitation.Id,
      FamilyId = invitation.FamilyId,
      FamilyName = family?.Name ?? "Unknown",
      InvitedEmail = invitation.InvitedEmail,
      InvitationToken = invitation.InvitationToken,
      InvitedRole = invitation.InvitedRole,
      InvitedRoleName = invitation.InvitedRole.ToString(),
      InvitedByUserId = invitation.InvitedByUserId,
      InvitedByUserName = invitedBy?.Username ?? "Unknown",
      CreatedAt = invitation.CreatedAt,
      ExpiresAt = invitation.ExpiresAt,
      IsExpired = invitation.ExpiresAt < DateTime.UtcNow,
      IsAccepted = invitation.IsAccepted,
      AcceptedAt = invitation.AcceptedAt
    };
  }

  private string GenerateInvitationToken()
  {
    return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
  }

  private string GetBaseUrl()
  {
    return _configuration["App:BaseUrl"] ?? "https://buoy-ai.com";
  }

  private string GetRoleName(FamilyMemberRole role)
  {
    return role switch
    {
      FamilyMemberRole.Owner => "Owner",
      FamilyMemberRole.BudgetAdmin => "Budget Admin",
      FamilyMemberRole.Member => "Member",
      FamilyMemberRole.ReadOnly => "Read Only",
      _ => role.ToString()
    };
  }

  /// <summary>
  /// Updates subscription tiers for all family members when the owner's subscription changes.
  /// </summary>
  public async Task UpdateFamilyMembersTierAsync(int ownerUserId, SubscriptionTier? newOwnerTier)
  {
    _logger.LogInformation("Updating family members tier for owner {OwnerId} to {NewTier}", 
      ownerUserId, newOwnerTier);

    var owner = await _userRepository.GetByIdAsync(ownerUserId);
    if (owner?.FamilyId == null)
    {
      _logger.LogDebug("User {OwnerId} is not in a family, skipping family tier update", ownerUserId);
      return;
    }

    // Verify this user is the family owner
    var family = await _familyRepository.GetByIdAsync(owner.FamilyId.Value);
    if (family == null || family.CreatedByUserId != ownerUserId)
    {
      _logger.LogDebug("User {OwnerId} is not the family owner, skipping family tier update", ownerUserId);
      return;
    }

    // Get all family members except the owner
    var familyMembers = await _familyMemberRepository.GetByFamilyIdAsync(owner.FamilyId.Value);
    var memberUserIds = familyMembers
      .Where(m => m.UserId != ownerUserId)
      .Select(m => m.UserId)
      .ToList();

    if (!memberUserIds.Any())
    {
      _logger.LogDebug("No other family members to update for family {FamilyId}", owner.FamilyId.Value);
      return;
    }

    // Determine if owner now has premium access.
    //
    // This must agree with the GRANT side in AcceptInvitationAsync, which asks
    // IEntitlementService.HasPremiumAccess. Judging on `newOwnerTier` alone was narrower than that:
    // it missed the two arms that carry coverage without sitting in the tier column, so an
    // owner could grant inherited premium at invite time and have this sweep strip it back
    // off while their coverage was still perfectly valid.
    //
    //   * Allowlisted operators — an admin-by-EMAIL owner may sit at Standard/null. The grant
    //     path honours the allowlist explicitly ("a family owned by an allowlisted operator
    //     still passes inherited premium down"); this sweep did not.
    //   * Active trials — there is no Trial tier, so a trialing owner's persisted tier is
    //     Standard/null and IsTrialActive carries the coverage.
    //
    // Callers that pass `null` to force a strip still strip: every lapse path clears
    // IsTrialActive and persists it BEFORE calling (see BillingService's trial-lapse handler
    // and TrialProvisioningService's sweep), so the trial arm is already false by then.
    //
    // The decision itself is IEntitlementService.HasPremiumAccess, so the two sides cannot drift.
    // It is asked about the NEW tier, which the caller has not necessarily persisted, so it is
    // given a probe carrying the new tier plus the owner's email and trial state.
    var ownerHasPremium = _entitlements.HasPremiumAccess(new User
    {
      Email = owner.Email,
      SubscriptionTier = newOwnerTier,
      IsTrialActive = owner.IsTrialActive,
      TrialEndDate = owner.TrialEndDate,
    });

    foreach (var memberId in memberUserIds)
    {
      var member = await _userRepository.GetByIdAsync(memberId);
      if (member == null) continue;

      if (ownerHasPremium)
      {
        // Grant inherited premium to family members who don't already have their own premium
        if (member.SubscriptionTier != SubscriptionTier.Premium && 
            member.SubscriptionTier != SubscriptionTier.Admin)
        {
          member.SubscriptionTier = SubscriptionTier.PremiumInherited;
          await _userRepository.UpdateAsync(member);
          _logger.LogInformation("Granted PremiumInherited tier to family member {MemberId}", memberId);
        }
      }
      else
      {
        // Remove inherited premium from family members
        if (member.SubscriptionTier == SubscriptionTier.PremiumInherited)
        {
          member.SubscriptionTier = null;
          await _userRepository.UpdateAsync(member);
          _logger.LogInformation("Removed PremiumInherited tier from family member {MemberId}", memberId);
        }
      }
    }

    _logger.LogInformation("Updated {Count} family members' tiers for family {FamilyId}", 
      memberUserIds.Count, owner.FamilyId.Value);
  }

  #endregion
}
