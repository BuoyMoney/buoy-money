using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class FamilyInvitationRepository : IFamilyInvitationRepository
{
  private readonly BuoyAIDbContext _context;

  public FamilyInvitationRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<FamilyInvitation?> GetByIdAsync(int id)
  {
    return await _context.FamilyInvitations
      .Include(i => i.Family)
      .Include(i => i.InvitedByUser)
      .FirstOrDefaultAsync(i => i.Id == id);
  }

  public async Task<FamilyInvitation?> GetByTokenAsync(string token)
  {
    return await _context.FamilyInvitations
      .Include(i => i.Family)
      .Include(i => i.InvitedByUser)
      .FirstOrDefaultAsync(i => i.InvitationToken == token && !i.IsAccepted && i.ExpiresAt > DateTime.UtcNow);
  }

  public async Task<FamilyInvitation?> GetByTokenAnyStateAsync(string token)
  {
    return await _context.FamilyInvitations
      .Include(i => i.Family)
      .Include(i => i.InvitedByUser)
      .FirstOrDefaultAsync(i => i.InvitationToken == token);
  }

  public async Task<IEnumerable<FamilyInvitation>> GetByFamilyIdAsync(int familyId)
  {
    return await _context.FamilyInvitations
      .Include(i => i.InvitedByUser)
      .Where(i => i.FamilyId == familyId)
      .ToListAsync();
  }

  public async Task<IEnumerable<FamilyInvitation>> GetPendingByFamilyIdAsync(int familyId)
  {
    return await _context.FamilyInvitations
      .Include(i => i.InvitedByUser)
      .Where(i => i.FamilyId == familyId && !i.IsAccepted && i.ExpiresAt > DateTime.UtcNow)
      .ToListAsync();
  }

  public async Task<IEnumerable<FamilyInvitation>> GetByEmailAsync(string email)
  {
    if (string.IsNullOrWhiteSpace(email))
      return new List<FamilyInvitation>();

    // Case-insensitive, like UserRepository.GetByEmailAsync: accounts are stored lowercased,
    // and invitations made before the invite address was normalised keep the owner's casing.
    var normalized = email.Trim().ToLower();
    return await _context.FamilyInvitations
      .Include(i => i.Family)
      .Include(i => i.InvitedByUser)
      .Where(i => i.InvitedEmail.ToLower() == normalized && !i.IsAccepted && i.ExpiresAt > DateTime.UtcNow)
      .ToListAsync();
  }

  public async Task<FamilyInvitation> CreateAsync(FamilyInvitation invitation)
  {
    invitation.CreatedAt = DateTime.UtcNow;
    
    _context.FamilyInvitations.Add(invitation);
    await _context.SaveChangesAsync();
    return invitation;
  }

  public async Task<FamilyInvitation> UpdateAsync(FamilyInvitation invitation)
  {
    _context.FamilyInvitations.Update(invitation);
    await _context.SaveChangesAsync();
    return invitation;
  }

  public async Task DeleteAsync(int id)
  {
    var invitation = await _context.FamilyInvitations.FindAsync(id);
    if (invitation != null)
    {
      _context.FamilyInvitations.Remove(invitation);
      await _context.SaveChangesAsync();
    }
  }

  public async Task DeleteExpiredAsync()
  {
    var expiredInvitations = await _context.FamilyInvitations
      .Where(i => !i.IsAccepted && i.ExpiresAt <= DateTime.UtcNow)
      .ToListAsync();
    
    _context.FamilyInvitations.RemoveRange(expiredInvitations);
    await _context.SaveChangesAsync();
  }
}
