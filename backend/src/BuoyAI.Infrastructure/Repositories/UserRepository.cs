using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
  private readonly BuoyAIDbContext _context;

  public UserRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<User?> GetByIdAsync(int id)
  {
    return await _context.Users
      .Include(u => u.Accounts)
      .Include(u => u.Budgets)
      .Include(u => u.PlaidConnections)
      .AsSplitQuery()
      .FirstOrDefaultAsync(u => u.Id == id);
  }

  public async Task<User?> GetByUsernameAsync(string username)
  {
    return await _context.Users
      .FirstOrDefaultAsync(u => u.Username == username);
  }

  public async Task<User?> GetByEmailAsync(string email)
  {
    if (string.IsNullOrWhiteSpace(email))
    {
      return null;
    }

    // Case-insensitive: email addresses are case-insensitive in practice and accounts are
    // stored lowercased, so an exact-case match let a differently-cased email (e.g. the raw
    // address from a Google ID token) miss an existing account — silently failing to link
    // Google to it (→ "no account" → registration) or allowing a case-variant duplicate.
    var normalized = email.Trim().ToLower();
    return await _context.Users
      .FirstOrDefaultAsync(u => u.Email.ToLower() == normalized);
  }

  public async Task<User?> GetByGoogleIdAsync(string googleId)
  {
    return await _context.Users
      .FirstOrDefaultAsync(u => u.GoogleId == googleId);
  }

  public async Task<User?> GetByAppleIdAsync(string appleId)
  {
    return await _context.Users
      .FirstOrDefaultAsync(u => u.AppleId == appleId);
  }

  public async Task<User> CreateAsync(User user)
  {
    user.CreatedAt = DateTime.UtcNow;
    user.UpdatedAt = DateTime.UtcNow;
    
    _context.Users.Add(user);
    await _context.SaveChangesAsync();
    return user;
  }

  public async Task<User> UpdateAsync(User user)
  {
    user.UpdatedAt = DateTime.UtcNow;
    
    _context.Users.Update(user);
    await _context.SaveChangesAsync();
    return user;
  }

  public async Task DeleteAsync(int id)
  {
    var user = await _context.Users.FindAsync(id);
    if (user != null)
    {
      _context.Users.Remove(user);
      await _context.SaveChangesAsync();
    }
  }

  public async Task<int> CountGoogleRegistrationsSinceAsync(DateTime since)
  {
    return await _context.Users
      .Where(u => u.IsGoogleUser && u.CreatedAt >= since)
      .CountAsync();
  }

  public async Task<int> GetRecentlyActiveUserCountAsync(TimeSpan recentWindow)
  {
    // Count users who have had successful login attempts within the recent window
    var cutoff = DateTime.UtcNow - recentWindow;
    return await _context.LoginAttempts
      .Where(la => la.AttemptedAt >= cutoff && la.Success)
      .Select(la => la.UserId)
      .Distinct()
      .CountAsync();
  }
}
