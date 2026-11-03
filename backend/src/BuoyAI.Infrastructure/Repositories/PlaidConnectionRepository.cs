using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Repositories;

public class PlaidConnectionRepository : IPlaidConnectionRepository
{
  private readonly BuoyAIDbContext _context;

  public PlaidConnectionRepository(BuoyAIDbContext context)
  {
    _context = context;
  }

  public async Task<IEnumerable<PlaidConnection>> GetByUserIdAsync(int userId)
  {
    return await _context.PlaidConnections
      .Where(pc => pc.UserId == userId && pc.IsActive)
      .ToListAsync();
  }

  public async Task<IEnumerable<PlaidConnection>> GetByUserOrFamilyAsync(int userId, int? familyId)
  {
    // If the user is in a family, return all active connections for that family
    // Otherwise, return only the user's own connections
    if (familyId.HasValue)
    {
      return await _context.PlaidConnections
        .Include(pc => pc.User)
        .Where(pc => pc.FamilyId == familyId.Value && pc.IsActive)
        .ToListAsync();
    }
    
    return await _context.PlaidConnections
      .Include(pc => pc.User)
      .Where(pc => pc.UserId == userId && pc.IsActive)
      .ToListAsync();
  }

  public async Task<PlaidConnection?> GetByIdAsync(int id)
  {
    return await _context.PlaidConnections
      .Include(pc => pc.User)
      .FirstOrDefaultAsync(pc => pc.Id == id);
  }

  public async Task<PlaidConnection?> GetByItemIdAsync(string itemId)
  {
    return await _context.PlaidConnections
      .FirstOrDefaultAsync(pc => pc.ItemId == itemId);
  }

  public async Task<PlaidConnection> CreateAsync(PlaidConnection connection)
  {
    connection.CreatedAt = DateTime.UtcNow;
    connection.UpdatedAt = DateTime.UtcNow;
    connection.LastSyncAt = DateTime.UtcNow;
    
    _context.PlaidConnections.Add(connection);
    await _context.SaveChangesAsync();
    return connection;
  }

  public async Task<PlaidConnection> UpdateAsync(PlaidConnection connection)
  {
    connection.UpdatedAt = DateTime.UtcNow;
    
    _context.PlaidConnections.Update(connection);
    await _context.SaveChangesAsync();
    return connection;
  }

  public async Task<bool> TryClaimAlertEmailAsync(int connectionId, DateTime sentAtUtc)
  {
    var rows = await _context.PlaidConnections
      .Where(pc => pc.Id == connectionId && pc.AlertSentAt == null)
      .ExecuteUpdateAsync(s => s.SetProperty(pc => pc.AlertSentAt, sentAtUtc));
    return rows > 0;
  }

  public async Task ReleaseAlertEmailClaimAsync(int connectionId)
  {
    await _context.PlaidConnections
      .Where(pc => pc.Id == connectionId)
      .ExecuteUpdateAsync(s => s.SetProperty(pc => pc.AlertSentAt, (DateTime?)null));
  }

  public async Task ClearAlertEmailClaimIfActiveAsync(int connectionId)
  {
    await _context.PlaidConnections
      .Where(pc => pc.Id == connectionId && pc.Status == PlaidItemStatus.Active)
      .ExecuteUpdateAsync(s => s.SetProperty(pc => pc.AlertSentAt, (DateTime?)null));
  }

  public async Task DeleteAsync(int id)
  {
    var connection = await _context.PlaidConnections.FindAsync(id);
    if (connection != null)
    {
      connection.IsActive = false;
      connection.UpdatedAt = DateTime.UtcNow;
      await _context.SaveChangesAsync();
    }
  }

  // Max historical-import attempts before we stop auto-retrying. Bounds a genuinely broken
  // import (e.g. a permanently-revoked token) so we don't loop forever, while still recovering
  // from the common transient cases below.
  private const int MaxImportAttempts = 5;

  // How long an import may sit InProgress before we consider the worker dead (container
  // redeploy / OOM kill) and requeue it. A full 2-year import runs in minutes, so 30m is safe.
  private static readonly TimeSpan StaleImportThreshold = TimeSpan.FromMinutes(30);

  public async Task<IEnumerable<PlaidConnection>> GetConnectionsNeedingBackgroundImport()
  {
    var staleCutoff = DateTime.UtcNow - StaleImportThreshold;
    return await _context.PlaidConnections
      .Where(pc => pc.IsActive &&
                   pc.Provider == BankProvider.Plaid && // the historical import is Plaid's; other providers import when they connect
                   (pc.ImportStatus == ImportStatus.Pending || // Fresh import (quick sync done or failed)
                    // Retry a Failed import (transient Plaid error) with a bounded attempt count
                    // so the connection isn't orphaned with no recovery path.
                    (pc.ImportStatus == ImportStatus.Failed && pc.ImportAttempts < MaxImportAttempts) ||
                    // Requeue an import stuck InProgress past the stale threshold — the worker
                    // that set it was killed mid-run (redeploy/OOM) and will never resume it.
                    (pc.ImportStatus == ImportStatus.InProgress &&
                     pc.ImportStartedAt != null && pc.ImportStartedAt < staleCutoff &&
                     pc.ImportAttempts < MaxImportAttempts)))
      .ToListAsync();
  }

  public async Task<IEnumerable<PlaidConnection>> GetBridgeConnectionsNeedingReconcileAsync()
  {
    return await _context.PlaidConnections
      .Where(pc => pc.Provider == BankProvider.Bridge && pc.Status == PlaidItemStatus.LoginRequired)
      .ToListAsync();
  }

  public async Task<IEnumerable<PlaidConnection>> GetActiveConnectionsForSync()
  {
    // Include completed imports, pending/in-progress imports that need retry, AND imports that
    // exhausted the historical-backfill retry cap (ImportStatus.Failed) — but ONLY for healthy
    // items. The `Status == PlaidItemStatus.Active` guard is what enforces "healthy": broken
    // statuses (LoginRequired / Error / PendingExpiration / PendingDisconnect / Disconnected)
    // are still excluded, so the 10-minute job never re-syncs an item whose token no longer
    // authenticates (which would otherwise hammer Plaid with an invalid token forever and leave
    // the user's data silently stale with no reconnect prompt).
    // Also include InProgress: a historical import that stalled (or is legitimately still
    // running) must not stop the 10-minute ongoing transaction sync, otherwise the account
    // silently stops receiving new transactions while the import is stuck.
    // Also include Failed: a historical BACKFILL that gave up (5 attempts exhausted, e.g. a
    // Plaid outage during the user's initial import) leaves the connection otherwise Active and
    // healthy. A failed backfill must NOT stop go-forward transaction sync — otherwise the
    // account silently stops receiving new transactions forever (the exact orphaned-connection
    // symptom). ImportStatus tracks only the one-time backfill; it does not indicate the item
    // itself is broken — that is what `Status` is for, and it is still filtered above.
    return await _context.PlaidConnections
      .Where(pc => pc.IsActive &&
                   (pc.Status == PlaidItemStatus.Active ||
                    // A Cloud bank feed that is only expiring or to be disconnected still works, so it keeps syncing
                    // while the user is told; Plaid's own pending states stay out as they always were.
                    (pc.Provider == BankProvider.Bridge &&
                     (pc.Status == PlaidItemStatus.PendingExpiration || pc.Status == PlaidItemStatus.PendingDisconnect))) &&
                   (pc.ImportStatus == ImportStatus.Completed ||
                    pc.ImportStatus == ImportStatus.Pending ||
                    pc.ImportStatus == ImportStatus.InProgress ||
                    pc.ImportStatus == ImportStatus.Failed))
      .ToListAsync();
  }
}
