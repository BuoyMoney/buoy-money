using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class FamilyRepository : IFamilyRepository
{
  private readonly BuoyAIDbContext _context;

  public FamilyRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<Family?> GetByIdAsync(int id)
  {
    return await _context.Families
      .Include(f => f.Members)
        .ThenInclude(m => m.User)
      .FirstOrDefaultAsync(f => f.Id == id);
  }

  public async Task<Family?> GetByUserIdAsync(int userId)
  {
    var user = await _context.Users.FindAsync(userId);
    if (user?.FamilyId == null)
      return null;

    return await GetByIdAsync(user.FamilyId.Value);
  }

  public async Task<Family> CreateAsync(Family family)
  {
    family.CreatedAt = DateTime.UtcNow;
    family.UpdatedAt = DateTime.UtcNow;
    
    _context.Families.Add(family);
    await _context.SaveChangesAsync();
    return family;
  }

  public async Task<Family> UpdateAsync(Family family)
  {
    family.UpdatedAt = DateTime.UtcNow;
    
    _context.Families.Update(family);
    await _context.SaveChangesAsync();
    return family;
  }

  public async Task DeleteAsync(int id)
  {
    var family = await _context.Families.FindAsync(id);
    if (family != null)
    {
      _context.Families.Remove(family);
      await _context.SaveChangesAsync();
    }
  }

  public async Task<IEnumerable<FamilyMember>> GetMembersAsync(int familyId)
  {
    return await _context.FamilyMembers
      .Include(m => m.User)
      .Where(m => m.FamilyId == familyId && m.IsActive)
      .ToListAsync();
  }

  public async Task<FamilyMember?> GetMemberAsync(int familyId, int userId)
  {
    return await _context.FamilyMembers
      .Include(m => m.User)
      .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.UserId == userId && m.IsActive);
  }

  public async Task<bool> IsUserInFamilyAsync(int userId, int familyId)
  {
    return await _context.FamilyMembers
      .AnyAsync(m => m.UserId == userId && m.FamilyId == familyId && m.IsActive);
  }
}
