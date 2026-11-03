using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class SubscriptionTransactionRepository : ISubscriptionTransactionRepository
{
  private readonly BuoyAIDbContext _context;

  public SubscriptionTransactionRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<IEnumerable<SubscriptionTransaction>> GetBySubscriptionIdAsync(int subscriptionId)
  {
    return await _context.SubscriptionTransactions
      .Where(st => st.SubscriptionId == subscriptionId)
      .Include(st => st.Transaction)
      .Include(st => st.Subscription)
      .OrderByDescending(st => st.CreatedAt)
      .ToListAsync();
  }

  public async Task<SubscriptionTransaction> CreateAsync(SubscriptionTransaction subscriptionTransaction)
  {
    subscriptionTransaction.CreatedAt = DateTime.UtcNow;
    
    _context.SubscriptionTransactions.Add(subscriptionTransaction);
    await _context.SaveChangesAsync();
    return subscriptionTransaction;
  }

  public async Task DeleteBySubscriptionIdAsync(int subscriptionId)
  {
    var subscriptionTransactions = await _context.SubscriptionTransactions
      .Where(st => st.SubscriptionId == subscriptionId)
      .ToListAsync();
    
    _context.SubscriptionTransactions.RemoveRange(subscriptionTransactions);
    await _context.SaveChangesAsync();
  }

  public async Task<HashSet<int>> GetRecurringTransactionIdsAsync(IEnumerable<int> transactionIds)
  {
    var ids = transactionIds.ToList();
    if (!ids.Any())
      return new HashSet<int>();

    var recurringIds = await _context.SubscriptionTransactions
      .Where(st => ids.Contains(st.TransactionId))
      .Select(st => st.TransactionId)
      .ToListAsync();

    return recurringIds.ToHashSet();
  }
}
