using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Utilities;

namespace BuoyAI.API.Controllers;

[Authorize]
public class FamilyController : BaseController
{
  private readonly IFamilyService _familyService;
  private readonly ILogger<FamilyController> _logger;

  public FamilyController(
    IFamilyService familyService,
    ILogger<FamilyController> logger)
  {
    _familyService = familyService;
    _logger = logger;
  }

  #region Family Management

  /// <summary>
  /// Get the user's current family
  /// </summary>
  [HttpGet]
  public async Task<IActionResult> GetFamily()
  {
    var userId = GetCurrentUserId();
    var family = await _familyService.GetFamilyAsync(userId);

    if (family == null)
    {
      return NotFound(new { message = "User is not in a family" });
    }

    return Ok(family);
  }

  /// <summary>
  /// Create a new family (user becomes owner)
  /// Requires Premium subscription
  /// </summary>
  [HttpPost]
  public async Task<IActionResult> CreateFamily([FromBody] CreateFamilyRequest request)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(request.Name))
      {
        return BadRequest(new { message = "Family name is required" });
      }

      var userId = GetCurrentUserId();
      var family = await _familyService.CreateFamilyAsync(userId, request);

      return CreatedAtAction(nameof(GetFamily), family);
    }
    catch (SubscriptionRequiredException ex)
    {
      return StatusCode(403, new { message = ex.Message, upgradeRequired = true, requiredTier = ex.RequiredTier });
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Update family settings (Owner only)
  /// </summary>
  [HttpPut]
  public async Task<IActionResult> UpdateFamily([FromBody] UpdateFamilyRequest request)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(request.Name))
      {
        return BadRequest(new { message = "Family name is required" });
      }

      var userId = GetCurrentUserId();
      var family = await _familyService.UpdateFamilyAsync(userId, request);

      return Ok(family);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Delete the family (Owner only)
  /// </summary>
  [HttpDelete]
  public async Task<IActionResult> DeleteFamily()
  {
    try
    {
      var userId = GetCurrentUserId();
      await _familyService.DeleteFamilyAsync(userId);

      return NoContent();
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Leave the family (non-owners only)
  /// </summary>
  [HttpPost("leave")]
  public async Task<IActionResult> LeaveFamily()
  {
    try
    {
      var userId = GetCurrentUserId();
      await _familyService.LeaveFamilyAsync(userId);

      return NoContent();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  #endregion

  #region Member Management

  /// <summary>
  /// Get all family members
  /// </summary>
  [HttpGet("members")]
  public async Task<IActionResult> GetFamilyMembers()
  {
    try
    {
      var userId = GetCurrentUserId();
      var members = await _familyService.GetFamilyMembersAsync(userId);

      return Ok(members);
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Update a member's role (Owner only)
  /// </summary>
  [HttpPut("members/{targetUserId}/role")]
  public async Task<IActionResult> UpdateMemberRole(
    int targetUserId,
    [FromBody] UpdateMemberRoleRequest request)
  {
    try
    {
      var userId = GetCurrentUserId();
      var member = await _familyService.UpdateMemberRoleAsync(userId, targetUserId, request.NewRole);

      return Ok(member);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Remove a member from the family (Owner only)
  /// </summary>
  [HttpDelete("members/{targetUserId}")]
  public async Task<IActionResult> RemoveMember(int targetUserId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _familyService.RemoveMemberAsync(userId, targetUserId);

      return NoContent();
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  #endregion

  #region Invitation Management

  /// <summary>
  /// Get pending invitations for the family (Owner only)
  /// </summary>
  [HttpGet("invitations")]
  public async Task<IActionResult> GetPendingInvitations()
  {
    try
    {
      var userId = GetCurrentUserId();
      var invitations = await _familyService.GetPendingInvitationsAsync(userId);

      return Ok(invitations);
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Get invitation details by token (public endpoint for accepting invitations)
  /// </summary>
  [HttpGet("invitations/{token}")]
  [AllowAnonymous]
  public async Task<IActionResult> GetInvitation(string token)
  {
    var invitation = await _familyService.GetInvitationByTokenAsync(token);

    if (invitation == null)
    {
      return NotFound(new { message = "Invitation not found or expired" });
    }

    return Ok(invitation);
  }

  /// <summary>
  /// Send an invitation to join the family (Owner only)
  /// Requires Premium subscription
  /// </summary>
  [HttpPost("invitations")]
  public async Task<IActionResult> InviteMember([FromBody] InviteFamilyMemberRequest request)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(request.Email))
      {
        return BadRequest(new { message = "Email is required" });
      }

      // Validate email format
      if (!EmailValidator.IsValidEmail(request.Email))
      {
        return BadRequest(new { message = "Invalid email format" });
      }

      var userId = GetCurrentUserId();
      var invitation = await _familyService.InviteMemberAsync(userId, request);

      return CreatedAtAction(
        nameof(GetInvitation),
        new { token = invitation.InvitationToken },
        invitation);
    }
    catch (SubscriptionRequiredException ex)
    {
      return StatusCode(403, new { message = ex.Message, upgradeRequired = true, requiredTier = ex.RequiredTier });
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Accept an invitation and join the family
  /// </summary>
  [HttpPost("invitations/{token}/accept")]
  public async Task<IActionResult> AcceptInvitation(string token)
  {
    try
    {
      var userId = GetCurrentUserId();
      var member = await _familyService.AcceptInvitationAsync(userId, token);

      return Ok(member);
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Cancel a pending invitation (Owner only)
  /// </summary>
  [HttpDelete("invitations/{invitationId}")]
  public async Task<IActionResult> CancelInvitation(int invitationId)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _familyService.CancelInvitationAsync(userId, invitationId);

      return NoContent();
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Decline/reject an invitation addressed to the current user
  /// </summary>
  [HttpDelete("invitations/{token}/decline")]
  public async Task<IActionResult> DeclineInvitation(string token)
  {
    try
    {
      var userId = GetCurrentUserId();
      await _familyService.DeclineInvitationAsync(userId, token);

      return NoContent();
    }
    catch (UnauthorizedAccessException)
    {
      return Forbid();
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Get pending invitations addressed to the current user's email
  /// </summary>
  [HttpGet("my-invitations")]
  public async Task<IActionResult> GetMyPendingInvitations()
  {
    var userId = GetCurrentUserId();
    var invitations = await _familyService.GetPendingInvitationsForUserAsync(userId);
    return Ok(invitations);
  }

  #endregion

  #region Helper Methods

  // Email validation moved to EmailValidator utility class

  #endregion
}
