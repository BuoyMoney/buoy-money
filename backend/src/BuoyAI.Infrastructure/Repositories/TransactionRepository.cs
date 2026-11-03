using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class TransactionRepository : ITransactionRepository
{
  private readonly BuoyAIDbContext _context;

  public TransactionRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<IEnumerable<Transaction>> GetByAccountIdAsync(int accountId)
  {
    return await _context.Transactions
      .Where(t => t.AccountId == accountId)
      .Include(t => t.Account)
      .Include(t => t.CategoryNav)
      .Include(t => t.AISuggestedCategoryNav)
      .Include(t => t.PersonalCategoryNav)
      .Include(t => t.SavingsGoal)
      .OrderByDescending(t => t.Date)
      .ToListAsync();
  }

  public async Task<IEnumerable<Transaction>> GetByUserIdAsync(int userId, DateTime? startDate = null, DateTime? endDate = null)
  {
    var query = _context.Transactions
      .Include(t => t.Account)
      .Include(t => t.CategoryNav)
      .Include(t => t.AISuggestedCategoryNav)
      .Include(t => t.PersonalCategoryNav)
      .Include(t => t.SavingsGoal)
      .Where(t => t.Account.UserId == userId);

    if (startDate.HasValue)
      query = query.Where(t => t.Date >= startDate.Value);

    if (endDate.HasValue)
      query = query.Where(t => t.Date <= endDate.Value);

    return await query
      .OrderByDescending(t => t.Date)
      .ToListAsync();
  }

  public async Task<IEnumerable<Transaction>> GetByUserOrFamilyAsync(int userId, int? familyId, DateTime? startDate = null, DateTime? endDate = null)
  {
    IQueryable<Transaction> query;

    if (familyId.HasValue)
    {
      // Return transactions owned by this user OR any account tagged with the family.
      // The OR guard ensures accounts synced before FamilyId was propagated are still visible.
      query = _context.Transactions
        .Include(t => t.Account)
          .ThenInclude(a => a.User)
        .Include(t => t.CategoryNav)
        .Include(t => t.AISuggestedCategoryNav)
        .Include(t => t.PersonalCategoryNav)
        .Include(t => t.SavingsGoal)
        .Where(t => t.Account.UserId == userId || t.Account.FamilyId == familyId.Value);
    }
    else
    {
      // No family — return only the user's own transactions
      query = _context.Transactions
        .Include(t => t.Account)
        .Include(t => t.CategoryNav)
        .Include(t => t.AISuggestedCategoryNav)
        .Include(t => t.PersonalCategoryNav)
        .Include(t => t.SavingsGoal)
        .Where(t => t.Account.UserId == userId);
    }

    if (startDate.HasValue)
      query = query.Where(t => t.Date >= startDate.Value);

    if (endDate.HasValue)
      query = query.Where(t => t.Date <= endDate.Value);

    return await query
      .OrderByDescending(t => t.Date)
      .ToListAsync();
  }

  public async Task<Transaction?> GetByIdAsync(int id)
  {
    return await _context.Transactions
      .Include(t => t.Account)
        .ThenInclude(a => a.User)
      .Include(t => t.CategoryNav)
      .Include(t => t.AISuggestedCategoryNav)
      .Include(t => t.PersonalCategoryNav)
      .Include(t => t.ClaimedByUser)
      .Include(t => t.SavingsGoal)
      .FirstOrDefaultAsync(t => t.Id == id);
  }

  public async Task<Transaction?> GetByIdWithSplitsAsync(int id)
  {
    return await _context.Transactions
      .Include(t => t.Account)
        .ThenInclude(a => a.User)
      .Include(t => t.CategoryNav)
      .Include(t => t.AISuggestedCategoryNav)
      .Include(t => t.PersonalCategoryNav)
      .Include(t => t.ClaimedByUser)
      .Include(t => t.SavingsGoal)
      .Include(t => t.CoveringTransfer)
        .ThenInclude(c => c!.Account)
      .Include(t => t.SplitTransactions)
        .ThenInclude(s => s.CategoryNav)
      .FirstOrDefaultAsync(t => t.Id == id);
  }

  public async Task<IEnumerable<Transaction>> GetUncoveredGoalWithdrawalsAsync(IEnumerable<int> goalIds)
  {
    var ids = goalIds.Distinct().ToList();
    if (ids.Count == 0) return Array.Empty<Transaction>();
    return await _context.Transactions
      .Include(t => t.Account)
      .Where(t => t.SavingsGoalId != null && ids.Contains(t.SavingsGoalId.Value)
                  && t.CoveringTransferTransactionId == null
                  && t.Amount < 0
                  && t.ParentTransactionId == null)
      .OrderBy(t => t.Date)
      .ToListAsync();
  }

  public async Task<Transaction?> GetByPlaidTransactionIdAsync(string plaidTransactionId)
  {
    return await _context.Transactions
      .FirstOrDefaultAsync(t => t.PlaidTransactionId == plaidTransactionId);
  }

  public async Task<Transaction> CreateAsync(Transaction transaction)
  {
    transaction.CreatedAt = DateTime.UtcNow;
    transaction.UpdatedAt = DateTime.UtcNow;
    
    _context.Transactions.Add(transaction);
    await _context.SaveChangesAsync();
    return transaction;
  }

  public async Task<Transaction> AddAsync(Transaction transaction)
  {
    transaction.CreatedAt = DateTime.UtcNow;
    transaction.UpdatedAt = DateTime.UtcNow;
    
    _context.Transactions.Add(transaction);
    await _context.SaveChangesAsync();
    return transaction;
  }

  public async Task<Transaction> UpdateAsync(Transaction transaction)
  {
    transaction.UpdatedAt = DateTime.UtcNow;

    _context.Transactions.Update(transaction);
    await _context.SaveChangesAsync();
    return transaction;
  }

  public async Task<int> ReattributeAccountClaimsAsync(int accountId, int? fromUserId, int? toUserId)
  {
    // Only rows that still track the OLD default move; explicit claims to a different member stay.
    var rows = await _context.Transactions
      .Where(t => t.AccountId == accountId
        && (fromUserId == null ? t.ClaimedByUserId == null : t.ClaimedByUserId == fromUserId))
      .ToListAsync();

    var now = DateTime.UtcNow;
    foreach (var t in rows)
    {
      t.ClaimedByUserId = toUserId;
      t.UpdatedAt = now;
    }
    await _context.SaveChangesAsync();
    return rows.Count;
  }

  public async Task<int> ClearNeedsReviewAsync(IEnumerable<int> transactionIds)
  {
    var ids = transactionIds.ToList();
    if (ids.Count == 0)
    {
      return 0;
    }

    // One load + one SaveChanges rather than a round trip per row: the backlog this is
    // built for is routinely hundreds of transactions.
    var rows = await _context.Transactions
      .Where(t => ids.Contains(t.Id))
      .ToListAsync();

    var now = DateTime.UtcNow;
    foreach (var t in rows)
    {
      t.NeedsReview = false;
      t.UpdatedAt = now;
    }
    await _context.SaveChangesAsync();
    return rows.Count;
  }

  public async Task<IEnumerable<Transaction>> CreateManyAsync(IEnumerable<Transaction> transactions)
  {
    var transactionList = transactions.ToList();
    var now = DateTime.UtcNow;
    
    foreach (var transaction in transactionList)
    {
      transaction.CreatedAt = now;
      transaction.UpdatedAt = now;
    }
    
    _context.Transactions.AddRange(transactionList);
    await _context.SaveChangesAsync();
    return transactionList;
  }

  public async Task DeleteAsync(int id)
  {
    var transaction = await _context.Transactions.FindAsync(id);
    if (transaction != null)
    {
      _context.Transactions.Remove(transaction);
      await _context.SaveChangesAsync();
    }
  }

  public async Task ExecuteInTransactionAsync(Func<Task> action)
  {
    // Already inside a caller's transaction (e.g. a receipt Apply splitting several charges):
    // join it, so the caller's commit or rollback covers this work too.
    if (_context.Database.CurrentTransaction != null)
    {
      await action();
      return;
    }
    // Wrap in the provider execution strategy so the transaction is retry-safe (mirrors the
    // BeginTransactionAsync + CreateExecutionStrategy pattern used in AuthService/seeders).
    var strategy = _context.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
      await using var transaction = await _context.Database.BeginTransactionAsync();
      try
      {
        await action();
        await transaction.CommitAsync();
      }
      catch
      {
        await transaction.RollbackAsync();
        throw;
      }
    });
  }

  public async Task DeleteAllByUserIdAsync(int userId)
  {
    var userTransactions = await _context.Transactions
      .Include(t => t.Account)
      .Where(t => t.Account.UserId == userId)
      .ToListAsync();

    if (userTransactions.Any())
    {
      _context.Transactions.RemoveRange(userTransactions);
      await _context.SaveChangesAsync();
    }
  }
}
