namespace BuoyAI.Core.DTOs.Plaid;

/// <summary>
/// Result of a transaction sync operation
/// </summary>
public class TransactionSyncResult
{
    /// <summary>
    /// Total number of new transactions synced
    /// </summary>
    public int TotalNewTransactions { get; set; }
    
    /// <summary>
    /// Number of transactions that need AI categorization (low Plaid confidence)
    /// </summary>
    public int TransactionsNeedingAI { get; set; }
    
    /// <summary>
    /// Number of new transactions a user rule categorized at ingest
    /// </summary>
    public int RulesApplied { get; set; }

    /// <summary>
    /// IDs of transactions that were queued for AI categorization
    /// </summary>
    public List<int> QueuedTransactionIds { get; set; } = new();
}
