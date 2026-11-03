using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Converts secrets written in the legacy AES-CBC format to the current authenticated format
/// ("v2:"), a small batch at a time. Covers every column that goes through
/// <see cref="IEncryptionService"/>: <c>PlaidConnections.AccessToken</c> and
/// <c>Users.AppleRefreshToken</c>, plus the two MFA columns that used to be plain text,
/// <c>Users.TotpSecret</c> and <c>Users.RecoveryCodes</c>.
///
/// Safe to run repeatedly and to interrupt: it only touches rows that are not yet "v2:", each
/// batch is its own save, and a row that can't be decrypted is logged (by id, never by value)
/// and left alone. Rows are walked by increasing id so an unreadable row can't be picked up
/// forever. Runs once per process start, after a short delay, on the copy holding the job lock.
/// </summary>
public class EncryptionReEncryptionService : BackgroundService
{
  public const int BatchSize = 50;

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly ILogger<EncryptionReEncryptionService> _logger;
  private readonly TimeSpan _startDelay;
  private readonly bool _enabled;

  // The delay outlasts the blue-green overlap (about a minute), so the old copy has stopped
  // before any row becomes unreadable to it. Set Encryption:ReEncryptEnabled=false to hold off.
  // The job also stays off until Encryption:WriteV2=true (step 2 of the rollout, see EncryptionService).
  public EncryptionReEncryptionService(
    IServiceScopeFactory scopeFactory,
    ILogger<EncryptionReEncryptionService> logger,
    IConfiguration configuration)
    : this(scopeFactory, logger, TimeSpan.FromMinutes(2), configuration.GetValue("Encryption:WriteV2", false) && configuration.GetValue("Encryption:ReEncryptEnabled", true))
  {
  }

  public EncryptionReEncryptionService(
    IServiceScopeFactory scopeFactory,
    ILogger<EncryptionReEncryptionService> logger,
    TimeSpan startDelay,
    bool enabled = true)
  {
    _scopeFactory = scopeFactory;
    _logger = logger;
    _startDelay = startDelay;
    _enabled = enabled;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    if (!_enabled)
    {
      _logger.LogInformation("Encryption re-encrypt pass off (needs Encryption:WriteV2=true and Encryption:ReEncryptEnabled not false)");
      return;
    }

    try
    {
      if (_startDelay > TimeSpan.Zero)
        await Task.Delay(_startDelay, stoppingToken);

      var result = await RunOnceAsync(stoppingToken);
      _logger.LogInformation(
        "Encryption re-encrypt pass finished: {Converted} converted, {AlreadyCurrent} already current, {Failed} unreadable",
        result.Converted, result.AlreadyCurrent, result.Failed);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      // shutdown mid-pass: the next start picks up whatever is left
    }
    catch (Exception ex)
    {
      // A failed pass must never take the API down; legacy rows still decrypt.
      _logger.LogError(ex, "Encryption re-encrypt pass failed; legacy rows stay readable and the next start retries");
    }
  }

  /// <summary>
  /// Test seam: runs after a MFA row is read and before it is written, so a test can change the
  /// row in between. Null in production.
  /// </summary>
  public Func<int, Task>? BeforeMfaWriteHook { get; set; }

  public readonly record struct Result(int Converted, int AlreadyCurrent, int Failed);

  /// <summary>One full pass over every encrypted column. Public so tests can drive it.</summary>
  public async Task<Result> RunOnceAsync(CancellationToken cancellationToken)
  {
    var plaid = await ConvertPlaidConnectionsAsync(cancellationToken);
    var apple = await ConvertAppleRefreshTokensAsync(cancellationToken);
    var totp = await ConvertPlaintextUserColumnAsync(
      "TOTP secret", nameof(User.TotpSecret), u => u.TotpSecret, (u, v) => u.TotpSecret = v, cancellationToken);
    var recovery = await ConvertPlaintextUserColumnAsync(
      "recovery codes", nameof(User.RecoveryCodes), u => u.RecoveryCodes, (u, v) => u.RecoveryCodes = v, cancellationToken);
    return new Result(
      plaid.Converted + apple.Converted + totp.Converted + recovery.Converted,
      plaid.AlreadyCurrent + apple.AlreadyCurrent + totp.AlreadyCurrent + recovery.AlreadyCurrent,
      plaid.Failed + apple.Failed + totp.Failed + recovery.Failed);
  }

  /// <summary>
  /// <c>Users.TotpSecret</c> and <c>Users.RecoveryCodes</c> were stored as plain text, never legacy
  /// ciphertext, so a row is converted by encrypting it as is. "v2:" marks the ones already done.
  /// Only reached when Encryption:WriteV2 is true, so Encrypt writes v2.
  /// </summary>
  private async Task<Result> ConvertPlaintextUserColumnAsync(
    string label,
    string column,
    Expression<Func<User, string?>> get,
    Action<User, string?> set,
    CancellationToken ct)
  {
    int converted = 0, failed = 0, lastId = 0;
    while (true)
    {
      ct.ThrowIfCancellationRequested();
      using var scope = _scopeFactory.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var crypto = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

      var read = get.Compile();
      var batch = await db.Users
        .AsNoTracking()
        .Where(u => u.Id > lastId && EF.Property<string?>(u, column) != null && EF.Property<string?>(u, column) != ""
                    && !EF.Property<string?>(u, column)!.StartsWith(EncryptionService.CurrentPrefix))
        .OrderBy(u => u.Id)
        .Take(BatchSize)
        .ToListAsync(ct);
      if (batch.Count == 0) break;

      var changed = 0;
      foreach (var row in batch)
      {
        lastId = row.Id;
        try
        {
          var old = read(row)!;
          var encrypted = crypto.Encrypt(old);
          if (BeforeMfaWriteHook != null) await BeforeMfaWriteHook(row.Id);
          if (await WriteIfUnchangedAsync(db, row.Id, get, set, old, encrypted, ct))
            changed++;
          else
            _logger.LogInformation("Re-encrypt: User {Id} {Label} changed while converting; left for the next pass", row.Id, label);
        }
        catch (Exception)
        {
          failed++;
          _logger.LogWarning("Re-encrypt: User {Id} {Label} could not be encrypted; left unchanged", row.Id, label);
        }
      }
      converted += changed;
      _logger.LogInformation("Re-encrypt: {Label} batch converted {Count} rows", label, changed);
    }

    using var countScope = _scopeFactory.CreateScope();
    var countDb = countScope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var already = await countDb.Users.CountAsync(
      u => EF.Property<string?>(u, column) != null && EF.Property<string?>(u, column)!.StartsWith(EncryptionService.CurrentPrefix), ct);
    return new Result(converted, already, failed);
  }

  /// <summary>
  /// Writes the encrypted value only if the column still holds what the job read. These columns
  /// carry single-use state (a spent recovery code, a freshly regenerated set, a newly scanned
  /// secret) and User has no concurrency token, so a blind save could put the old value back.
  /// Returns false when the row changed or vanished; the next pass sees it again.
  /// A request that loaded the row before this write and saves the whole User afterwards can put the
  /// plaintext back; that is harmless (plaintext is still accepted on read) and the next pass converts it again.
  /// </summary>
  private static async Task<bool> WriteIfUnchangedAsync(
    BuoyAIDbContext db, int id, Expression<Func<User, string?>> prop, Action<User, string?> set,
    string old, string encrypted, CancellationToken ct)
  {
    if (db.Database.IsRelational())
    {
      var unchanged = Expression.Lambda<Func<User, bool>>(
        Expression.Equal(prop.Body, Expression.Constant(old, typeof(string))), prop.Parameters);
      var rows = await db.Users
        .Where(u => u.Id == id)
        .Where(unchanged)
        .ExecuteUpdateAsync(s => s.SetProperty(prop, encrypted), ct);
      return rows > 0;
    }

    // Non-relational providers (unit tests) have no ExecuteUpdate: re-read, compare, save.
    var current = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
    if (current == null || prop.Compile()(current) != old) return false;
    set(current, encrypted);
    await db.SaveChangesAsync(ct);
    return true;
  }

  private async Task<Result> ConvertPlaidConnectionsAsync(CancellationToken ct)
  {
    int converted = 0, failed = 0, lastId = 0;
    while (true)
    {
      ct.ThrowIfCancellationRequested();
      using var scope = _scopeFactory.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var crypto = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

      var batch = await db.PlaidConnections
        .Where(c => c.Id > lastId && c.AccessToken != "" && !c.AccessToken.StartsWith(EncryptionService.CurrentPrefix))
        .OrderBy(c => c.Id)
        .Take(BatchSize)
        .ToListAsync(ct);
      if (batch.Count == 0) break;

      var changed = 0;
      foreach (var row in batch)
      {
        lastId = row.Id;
        try
        {
          var original = row.AccessToken;
          var reEncrypted = crypto.Encrypt(crypto.Decrypt(original));
          // Conditional on the ciphertext we read: if an item-update stored a new token since,
          // zero rows match and the newer token is kept (the next pass picks it up if legacy).
          var updated = await db.PlaidConnections
            .Where(c => c.Id == row.Id && c.AccessToken == original)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AccessToken, reEncrypted), ct);
          if (updated > 0) changed++;
        }
        catch (Exception)
        {
          failed++;
          _logger.LogWarning("Re-encrypt: PlaidConnection {Id} could not be decrypted; left unchanged", row.Id);
        }
      }
      converted += changed;
      _logger.LogInformation("Re-encrypt: PlaidConnections batch converted {Count} rows", changed);
    }

    var already = await CountCurrentPlaidAsync(ct);
    return new Result(converted, already, failed);
  }

  private async Task<Result> ConvertAppleRefreshTokensAsync(CancellationToken ct)
  {
    int converted = 0, failed = 0, lastId = 0;
    while (true)
    {
      ct.ThrowIfCancellationRequested();
      using var scope = _scopeFactory.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var crypto = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

      var batch = await db.Users
        .Where(u => u.Id > lastId && u.AppleRefreshToken != null && u.AppleRefreshToken != ""
                    && !u.AppleRefreshToken.StartsWith(EncryptionService.CurrentPrefix))
        .OrderBy(u => u.Id)
        .Take(BatchSize)
        .ToListAsync(ct);
      if (batch.Count == 0) break;

      var changed = 0;
      foreach (var row in batch)
      {
        lastId = row.Id;
        try
        {
          var original = row.AppleRefreshToken!;
          var reEncrypted = crypto.Encrypt(crypto.Decrypt(original));
          var updated = await db.Users
            .Where(u => u.Id == row.Id && u.AppleRefreshToken == original)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AppleRefreshToken, reEncrypted), ct);
          if (updated > 0) changed++;
        }
        catch (Exception)
        {
          failed++;
          _logger.LogWarning("Re-encrypt: User {Id} Apple refresh token could not be decrypted; left unchanged", row.Id);
        }
      }
      converted += changed;
      _logger.LogInformation("Re-encrypt: Apple refresh token batch converted {Count} rows", changed);
    }

    using var countScope = _scopeFactory.CreateScope();
    var countDb = countScope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var already = await countDb.Users.CountAsync(
      u => u.AppleRefreshToken != null && u.AppleRefreshToken.StartsWith(EncryptionService.CurrentPrefix), ct);
    return new Result(converted, already, failed);
  }

  private async Task<int> CountCurrentPlaidAsync(CancellationToken ct)
  {
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    return await db.PlaidConnections.CountAsync(
      c => c.AccessToken.StartsWith(EncryptionService.CurrentPrefix), ct);
  }
}
