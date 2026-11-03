using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class FamilyMemberRepository : IFamilyMemberRepository
{
  private readonly BuoyAIDbContext _context;

  public FamilyMemberRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<FamilyMember?> GetByIdAsync(int id)
  {
    return await _context.FamilyMembers
      .Include(m => m.User)
      .Include(m => m.Family)
      .FirstOrDefaultAsync(m => m.Id == id);
  }

  public async Task<FamilyMember?> GetByUserIdAsync(int userId)
  {
    return await _context.FamilyMembers
      .Include(m => m.Family)
      .FirstOrDefaultAsync(m => m.UserId == userId && m.IsActive);
  }

  public async Task<FamilyMember?> GetByUserAndFamilyAnyStateAsync(int userId, int familyId)
  {
    return await _context.FamilyMembers
      .FirstOrDefaultAsync(m => m.UserId == userId && m.FamilyId == familyId);
  }

  public async Task<IEnumerable<FamilyMember>> GetByFamilyIdAsync(int familyId)
  {
    return await _context.FamilyMembers
      .Include(m => m.User)
      .Where(m => m.FamilyId == familyId && m.IsActive)
      .ToListAsync();
  }

  public async Task<FamilyMember> CreateAsync(FamilyMember member)
  {
    member.JoinedAt = DateTime.UtcNow;
    
    _context.FamilyMembers.Add(member);
    await _context.SaveChangesAsync();
    return member;
  }

  public async Task<FamilyMember> UpdateAsync(FamilyMember member)
  {
    _context.FamilyMembers.Update(member);
    await _context.SaveChangesAsync();
    return member;
  }

  public async Task DeleteAsync(int id)
  {
    var member = await _context.FamilyMembers.FindAsync(id);
    if (member != null)
    {
      member.IsActive = false;
      await _context.SaveChangesAsync();
    }
  }

  public async Task RemoveMemberAsync(int familyId, int userId)
  {
    var member = await _context.FamilyMembers
      .FirstOrDefaultAsync(m => m.FamilyId == familyId && m.UserId == userId);
    
    if (member != null)
    {
      member.IsActive = false;
      await _context.SaveChangesAsync();
    }
  }
}
