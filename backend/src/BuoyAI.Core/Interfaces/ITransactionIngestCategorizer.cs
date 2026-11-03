using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Outcome of running the ingest-time categorization policy over a batch of freshly
/// persisted transactions.
/// </summary>
public class IngestCategorizationResult
{
  /// <summary>Rows a user rule claimed (deterministic, no AI needed).</summary>
  public int RulesApplied { get; set; }

  /// <summary>Rows handed to the AI categorization queue.</summary>
  public int QueuedForAi { get; set; }

  /// <summary>
  /// Rows kept on Plaid's VERY_HIGH-confidence category without an AI pass, because the
  /// user has never contradicted Plaid for that merchant.
  /// </summary>
  public int AcceptedFromPlaid { get; set; }

  /// <summary>
  /// Rows dated before the auto-categorization window (history from a first import). They
  /// keep whatever Plaid decided and are never sent to AI automatically.
  /// </summary>
  public int OutsideWindow { get; set; }

  /// <summary>Transaction ids that were queued, for callers that report them.</summary>
  public List<int> QueuedTransactionIds { get; set; } = new();
}

/// <summary>
/// The single place the categorization priority order is enforced for new rows, on every
/// path that ingests them (manual sync, login sync, webhook sync, the 10-minute background
/// sync and the first historical import).
///
/// Order: user rules win outright; then Plaid's VERY_HIGH call is trusted until the user has
/// proven it wrong for that merchant; everything else goes to the AI queue. Only rows inside
/// the auto-categorization window (this month and last) are queued, so a two-year first
/// import does not become two years of AI calls.
/// </summary>
public interface ITransactionIngestCategorizer
{
  /// <summary>
  /// Applies rules to every new row and queues the rest per the policy above. Never throws
  /// for policy reasons; the sync that created the rows must not fail because categorization
  /// bookkeeping did.
  /// </summary>
  Task<IngestCategorizationResult> ProcessIngestedAsync(
    IReadOnlyCollection<Transaction> newTransactions,
    CancellationToken cancellationToken = default);
}

/// <summary>
/// Wakes the AI categorization queue drainer for a user as soon as something is queued, so
/// freshly synced transactions get their suggestions within seconds rather than on the next
/// sweep. Enqueue-only; the drainer owns the reader.
/// </summary>
public interface IAICategorizationQueueSignal
{
  void Notify(int userId);
}
