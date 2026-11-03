using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class AccountRepository : IAccountRepository
{
  private readonly BuoyAIDbContext _context;

  public AccountRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<IEnumerable<Account>> GetByUserIdAsync(int userId)
  {
    return await _context.Accounts
      .Where(a => a.UserId == userId && a.IsActive)
      .Include(a => a.Transactions)
      .ToListAsync();
  }

  public async Task<IEnumerable<Account>> GetByFamilyIdAsync(int familyId)
  {
    return await _context.Accounts
      .Where(a => a.FamilyId == familyId && a.IsActive)
      .Include(a => a.Transactions)
      .Include(a => a.User)
      .ToListAsync();
  }

  public async Task<IEnumerable<Account>> GetByUserOrFamilyAsync(int userId, int? familyId)
  {
    // If user is in a family, return all family accounts
    // Otherwise, return only user's own accounts
    if (familyId.HasValue)
    {
      return await _context.Accounts
        .Where(a => a.FamilyId == familyId.Value && a.IsActive)
        .Include(a => a.Transactions)
        .Include(a => a.User)
        .ToListAsync();
    }

    return await _context.Accounts
      .Where(a => a.UserId == userId && a.IsActive)
      .Include(a => a.Transactions)
      .ToListAsync();
  }

  public async Task<IEnumerable<Account>> GetByConnectionIdAsync(int connectionId)
  {
    return await _context.Accounts
      .Where(a => a.PlaidConnectionId == connectionId && a.IsActive)
      .Include(a => a.Transactions)
      .ToListAsync();
  }

  public async Task<Dictionary<int, int>> CountActiveByConnectionIdsAsync(IReadOnlyCollection<int> connectionIds)
  {
    return await _context.Accounts
      .Where(a => a.PlaidConnectionId != null && connectionIds.Contains(a.PlaidConnectionId.Value) && a.IsActive)
      .GroupBy(a => a.PlaidConnectionId!.Value)
      .Select(g => new { Id = g.Key, Count = g.Count() })
      .ToDictionaryAsync(x => x.Id, x => x.Count);
  }

  public async Task<Account?> GetByIdAsync(int id)
  {
    return await _context.Accounts
      .Include(a => a.User)
      .Include(a => a.Transactions)
      .FirstOrDefaultAsync(a => a.Id == id);
  }

  public async Task<Account?> GetByPlaidAccountIdAsync(string plaidAccountId)
  {
    return await _context.Accounts
      .FirstOrDefaultAsync(a => a.PlaidAccountId == plaidAccountId);
  }

  public async Task<Account> CreateAsync(Account account)
  {
    account.CreatedAt = DateTime.UtcNow;
    account.UpdatedAt = DateTime.UtcNow;
    
    _context.Accounts.Add(account);
    await _context.SaveChangesAsync();
    return account;
  }

  public async Task<Account> UpdateAsync(Account account)
  {
    account.UpdatedAt = DateTime.UtcNow;
    
    _context.Accounts.Update(account);
    await _context.SaveChangesAsync();
    return account;
  }

  public async Task DeleteAsync(int id)
  {
    var account = await _context.Accounts.FindAsync(id);
    if (account != null)
    {
      account.IsActive = false;
      account.UpdatedAt = DateTime.UtcNow;
      await _context.SaveChangesAsync();
    }
  }
}
