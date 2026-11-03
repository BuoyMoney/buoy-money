using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class SubscriptionRepository : ISubscriptionRepository
{
  private readonly BuoyAIDbContext _context;

  public SubscriptionRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<IEnumerable<Subscription>> GetByUserIdAsync(int userId)
  {
    return await _context.Subscriptions
      .Where(s => s.UserId == userId)
      .Include(s => s.User)
      .Include(s => s.LinkedTransaction)
      .OrderBy(s => s.NextDueDate)
      .ToListAsync();
  }

  public async Task<Subscription?> GetByIdAsync(int id)
  {
    return await _context.Subscriptions
      .Include(s => s.User)
      .Include(s => s.LinkedTransaction)
      .Include(s => s.SubscriptionTransactions)
        .ThenInclude(st => st.Transaction)
      .FirstOrDefaultAsync(s => s.Id == id);
  }

  public async Task<Subscription> CreateAsync(Subscription subscription)
  {
    subscription.CreatedAt = DateTime.UtcNow;
    subscription.UpdatedAt = DateTime.UtcNow;
    
    _context.Subscriptions.Add(subscription);
    await _context.SaveChangesAsync();
    return subscription;
  }

  public async Task<Subscription> UpdateAsync(Subscription subscription)
  {
    subscription.UpdatedAt = DateTime.UtcNow;
    
    _context.Subscriptions.Update(subscription);
    await _context.SaveChangesAsync();
    return subscription;
  }

  public async Task DeleteAsync(int id)
  {
    var subscription = await _context.Subscriptions.FindAsync(id);
    if (subscription != null)
    {
      _context.Subscriptions.Remove(subscription);
      await _context.SaveChangesAsync();
    }
  }

  public async Task<IEnumerable<Subscription>> GetByUserIdAndDateRangeAsync(int userId, DateTime startDate, DateTime endDate)
  {
    return await _context.Subscriptions
      .Where(s => s.UserId == userId && 
                  s.NextDueDate >= startDate && 
                  s.NextDueDate <= endDate)
      .Include(s => s.User)
      .Include(s => s.LinkedTransaction)
      .OrderBy(s => s.NextDueDate)
      .ToListAsync();
  }

  public async Task<IEnumerable<Subscription>> GetActiveByUserIdAsync(int userId)
  {
    return await _context.Subscriptions
      .Where(s => s.UserId == userId && s.Status == Core.Enums.SubscriptionStatus.Active)
      .Include(s => s.User)
      .Include(s => s.LinkedTransaction)
      .OrderBy(s => s.NextDueDate)
      .ToListAsync();
  }
}
