using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class PlaidConnection
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public int? FamilyId { get; set; } // Foreign key to Family

  // Which aggregator this connection came from (#310). Defaults to Plaid — every pre-existing row
  // is Plaid (migration 0016 backfills to 0). The background sync + lifecycle dispatch on this via
  // IBankAggregatorRegistry. A non-Plaid provider stores its connection id in ItemId and its own
  // (encrypted) auth secret in AccessToken.
  public BankProvider Provider { get; set; } = BankProvider.Plaid;

  public string AccessToken { get; set; } = string.Empty; // Encrypted
  public string ItemId { get; set; } = string.Empty;
  public string InstitutionId { get; set; } = string.Empty;
  public string InstitutionName { get; set; } = string.Empty;
  public bool IsActive { get; set; } = true;
  public DateTime LastSyncAt { get; set; }
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Import Status Tracking
  public ImportStatus ImportStatus { get; set; } = ImportStatus.Pending;
  public int ImportProgress { get; set; } = 0; // Percentage (0-100)
  public DateTime? ImportStartedAt { get; set; }
  public DateTime? ImportCompletedAt { get; set; }
  public string? ImportMessage { get; set; }
  public DateTime? QuickSyncCompletedAt { get; set; } // When 30-day sync finished
  public int ImportAttempts { get; set; } = 0; // Bounded retry counter for the historical import

  // Transaction Sync Tracking
  public DateTime? LastTransactionSyncAt { get; set; } // Last successful transaction sync
  public int? LastSyncTransactionCount { get; set; } // Number of transactions synced in last sync
  public string? LastSyncError { get; set; } // Last sync error message
  public DateTime? LastSyncAttemptAt { get; set; } // Last sync attempt (even if failed)

  // Update Mode properties
  public PlaidItemStatus Status { get; set; } = PlaidItemStatus.Active;
  public string? LastError { get; set; }
  public DateTime? ExpirationDate { get; set; }
  public DateTime? DisconnectDate { get; set; }
  public DateTime? LastStatusUpdate { get; set; }
  // Set once the Plaid Item has been revoked at Plaid (/item/remove), so ongoing
  // Transactions billing is stopped. Distinct from IsActive (which is overloaded for
  // login-required / permission-revoked / locally-disconnected states) — this durably
  // records that the churn sweep already removed the item, making that sweep idempotent.
  public DateTime? PlaidRemovedAt { get; set; }
  public bool RequiresUserAction { get; set; } = false;
  public string? UpdateModeUrl { get; set; } // Link for user to reconnect
  public int ConsecutiveFailures { get; set; } = 0;

  // When the "your bank connection needs attention" email last went out for this connection.
  // Stops the same outage from emailing twice (a webhook can repeat). Cleared when a sync
  // succeeds again, so the next outage alerts again. Provider-neutral.
  public DateTime? AlertSentAt { get; set; }

  // Bridge only: the time of the Cloud's last heads-up (expiring, will be disconnected, new accounts) this server
  // already alerted the user about. The Cloud keeps a heads-up until a reconnect clears it, so this is what stops
  // every sync from alerting again.
  public DateTime? BridgeNoticeAt { get; set; }

  // Navigation properties
  public User User { get; set; } = null!;
  public Family? Family { get; set; }
}
