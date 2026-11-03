using System.Security.Cryptography;
using System.Text;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Services;

public record RegistrationStatus(bool FirstAccount, bool Open)
{
  /// <summary>True when a person with no invite can create an account right now.</summary>
  public bool CanRegister => FirstAccount || Open;
}

/// <summary>
/// Serializes the "is this the first account?" decision across concurrent sign-ups. Holds a
/// Postgres transaction-scoped advisory lock until the caller's transaction ends, so two
/// registrations on a fresh database cannot both become the instance admin. A no-op without a
/// relational provider or an open transaction (the InMemory test provider has neither locks nor
/// concurrency to guard).
/// </summary>
public class FirstAccountLock
{
  /// <summary>Fixed key for the advisory lock ("BUOYADMN" as a number).</summary>
  public const long Key = 0x42554F5941444D4E;

  public virtual async Task AcquireAsync(BuoyAIDbContext context)
  {
    if (!context.Database.IsRelational() || context.Database.CurrentTransaction == null) return;
    await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", Key);
  }
}

/// <summary>
/// Who may create an account on a self-hosted server. The first account is always allowed and
/// becomes the admin. After that registration is closed unless an admin opens it, or the person
/// brings an invite code an admin made. Registered only in the self-host edition, so hosted
/// sign-up is untouched.
/// </summary>
public class RegistrationGate
{
  public const string OpenKey = "registration_open";
  private const string InviteGoneMessage = "That invite code is not valid, has expired, or was already used.";
  private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I

  private readonly BuoyAIDbContext _context;

  private readonly IConfiguration? _configuration;
  private readonly FirstAccountLock _lock;

  public RegistrationGate(BuoyAIDbContext context, IConfiguration? configuration = null, FirstAccountLock? firstAccountLock = null)
  {
    _context = context;
    _configuration = configuration;
    _lock = firstAccountLock ?? new FirstAccountLock();
  }

  private bool HasOperatorList =>
    _configuration?.GetSection("Admin:Emails").Get<string[]>()?.Any(e => !string.IsNullOrWhiteSpace(e)) == true;

  private bool IsOperatorEmail(string? email) =>
    _configuration != null && AdminPolicy.IsAdminEmail(_configuration, email);

  public async Task<RegistrationStatus> GetStatusAsync()
  {
    var first = !await _context.Users.IgnoreQueryFilters().AnyAsync();
    var setting = await _context.InstanceSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == OpenKey);
    return new RegistrationStatus(first, setting?.Value == "true");
  }

  public async Task SetOpenAsync(bool open)
  {
    var setting = await _context.InstanceSettings.FirstOrDefaultAsync(s => s.Key == OpenKey);
    if (setting == null)
    {
      _context.InstanceSettings.Add(new InstanceSetting { Key = OpenKey, Value = open ? "true" : "false" });
    }
    else
    {
      setting.Value = open ? "true" : "false";
      setting.UpdatedAt = DateTime.UtcNow;
    }
    await _context.SaveChangesAsync();
  }

  /// <summary>
  /// Decides whether this new account may be created. Marks the first account as the admin.
  /// Returns the id of the invite it used (spend it with <see cref="ConsumeInviteAsync"/> once the
  /// account exists), or null when no invite was needed. Throws when registration is closed.
  /// </summary>
  public async Task<int?> AdmitAsync(User user, string? inviteCode)
  {
    // Must run inside the caller's user-creating transaction: the lock is held until it commits,
    // so the next registration sees this account and no longer counts as the first.
    await _lock.AcquireAsync(_context);
    var status = await GetStatusAsync();
    var operatorEmail = IsOperatorEmail(user.Email);

    // The operator's own address (Admin:Emails) can always register, even while sign-up is closed.
    if (operatorEmail)
    {
      user.IsInstanceAdmin = true;
      return null;
    }

    if (status.FirstAccount)
    {
      // With an operator list set, a stranger who reaches a fresh server first must not become
      // the admin. Only a listed address can claim it.
      if (HasOperatorList)
      {
        throw new RegistrationClosedException(
          "This server is waiting for its owner to create the first account. Use the admin email set for this server.");
      }
      user.IsInstanceAdmin = true;
      return null;
    }

    if (!string.IsNullOrWhiteSpace(inviteCode))
    {
      var hash = HashCode(inviteCode);
      var invite = await _context.RegistrationInvites.FirstOrDefaultAsync(i => i.CodeHash == hash);
      if (invite != null && invite.UsedAt == null && invite.ExpiresAt > DateTime.UtcNow)
      {
        return invite.Id;
      }
    }

    if (status.Open) return null;

    throw new RegistrationClosedException(string.IsNullOrWhiteSpace(inviteCode)
      ? "This server is not accepting new accounts. Ask the person who runs it for an invite code."
      : InviteGoneMessage);
  }

  public async Task ConsumeInviteAsync(int? inviteId, int userId)
  {
    if (inviteId == null) return;
    var invite = await _context.RegistrationInvites.FirstOrDefaultAsync(i => i.Id == inviteId);
    if (invite == null) return;
    if (invite.UsedAt != null) throw new RegistrationClosedException(InviteGoneMessage);
    invite.UsedByUserId = userId;
    invite.UsedAt = DateTime.UtcNow;
    try
    {
      // UsedAt is a concurrency token: the update only lands while used_at is still null.
      await _context.SaveChangesAsync();
    }
    catch (DbUpdateConcurrencyException)
    {
      // Someone else spent this code a moment ago. The caller's transaction rolls the account back.
      throw new RegistrationClosedException(InviteGoneMessage);
    }
  }

  /// <summary>Creates an invite. The plain code is returned once and only its hash is stored.</summary>
  public async Task<(RegistrationInvite Invite, string Code)> CreateInviteAsync(int adminUserId, string? note, int validDays)
  {
    var code = NewCode();
    var invite = new RegistrationInvite
    {
      CodeHash = HashCode(code),
      Note = string.IsNullOrWhiteSpace(note) ? null : SafeText.Truncate(note.Trim(), 200),
      CreatedByUserId = adminUserId,
      CreatedAt = DateTime.UtcNow,
      ExpiresAt = DateTime.UtcNow.AddDays(Math.Clamp(validDays, 1, 90)),
    };
    _context.RegistrationInvites.Add(invite);
    await _context.SaveChangesAsync();
    return (invite, code);
  }

  public Task<List<RegistrationInvite>> ListInvitesAsync() =>
    _context.RegistrationInvites.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(100).ToListAsync();

  public async Task<bool> RevokeInviteAsync(int id)
  {
    var invite = await _context.RegistrationInvites.FirstOrDefaultAsync(i => i.Id == id && i.UsedAt == null);
    if (invite == null) return false;
    _context.RegistrationInvites.Remove(invite);
    await _context.SaveChangesAsync();
    return true;
  }

  /// <summary>Case, dashes and spaces do not matter when someone types a code.</summary>
  public static string HashCode(string code)
  {
    var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
  }

  /// <summary>20 characters (about 100 bits) in four groups: XXXXX-XXXXX-XXXXX-XXXXX.</summary>
  public static string NewCode()
  {
    var chars = new char[20];
    for (var i = 0; i < chars.Length; i++) chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
    var s = new string(chars);
    return $"{s[..5]}-{s[5..10]}-{s[10..15]}-{s[15..]}";
  }
}
