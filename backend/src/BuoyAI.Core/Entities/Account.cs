using System.ComponentModel.DataAnnotations.Schema;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class Account
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public int? FamilyId { get; set; } // Foreign key to Family

  /// <summary>
  /// Whose budget this account's transactions feed by default. null = the family budget
  /// (shared card). Distinct from UserId (who connected the account): the family's main card is
  /// owned by an admin but associated with no one, so its spend defaults to the family budget.
  /// This only sets the default at ingest time — a transaction can be re-claimed to anyone.
  /// </summary>
  public int? AssociatedUserId { get; set; }
  public int? PlaidConnectionId { get; set; } // Foreign key to PlaidConnection
  public string PlaidAccountId { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string OfficialName { get; set; } = string.Empty;
  public string? Nickname { get; set; } // User-defined custom name for the account

  /// <summary>
  /// What to show the user for this account: their nickname if they set one, else the
  /// bank-provided name. Always prefer this over <see cref="Name"/> in anything
  /// user-facing — Plaid names are frequently useless for telling accounts apart (three
  /// separate cards can all arrive as "CREDIT CARD"), which is the whole point of the
  /// nickname. Computed, never persisted.
  /// </summary>
  [NotMapped]
  public string DisplayName =>
    !string.IsNullOrWhiteSpace(Nickname) ? Nickname!
    : !string.IsNullOrWhiteSpace(Name) ? Name
    : !string.IsNullOrWhiteSpace(OfficialName) ? OfficialName
    : $"Account {Id}";
  public string? Mask { get; set; } // Last 4 digits of account number
  public AccountType Type { get; set; }
  public string Subtype { get; set; } = string.Empty;
  public decimal CurrentBalance { get; set; }
  public decimal AvailableBalance { get; set; }
  public string Currency { get; set; } = "USD";
  public bool IsActive { get; set; } = true;
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Navigation properties
  public User User { get; set; } = null!;
  public User? AssociatedUser { get; set; }
  public Family? Family { get; set; }
  public PlaidConnection? PlaidConnection { get; set; }
  public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
