namespace BuoyAI.Core.Entities;

/// <summary>
/// Queue item for background AI transaction categorization.
/// Transactions are added to this queue after Plaid sync, then processed in batches.
/// </summary>
public class AICategorizationQueueItem
{
    public int Id { get; set; }
    
    /// <summary>
    /// The transaction to categorize
    /// </summary>
    public int TransactionId { get; set; }
    public Transaction Transaction { get; set; } = null!;
    
    /// <summary>
    /// The user who owns this transaction (for batch processing by user)
    /// </summary>
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    
    /// <summary>
    /// When this item was added to the queue
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When processing was attempted (null if not yet processed)
    /// </summary>
    public DateTime? ProcessedAt { get; set; }
    
    /// <summary>
    /// Status of the queue item
    /// </summary>
    public AICategorizationQueueStatus Status { get; set; } = AICategorizationQueueStatus.Pending;
    
    /// <summary>
    /// Number of retry attempts for failed items
    /// </summary>
    public int RetryCount { get; set; } = 0;
    
    /// <summary>
    /// Error message if processing failed
    /// </summary>
    public string? ErrorMessage { get; set; }
}

public enum AICategorizationQueueStatus
{
    /// <summary>
    /// Waiting to be processed
    /// </summary>
    Pending = 0,
    
    /// <summary>
    /// Currently being processed
    /// </summary>
    Processing = 1,
    
    /// <summary>
    /// Successfully processed
    /// </summary>
    Completed = 2,
    
    /// <summary>
    /// Failed to process (may retry)
    /// </summary>
    Failed = 3,
    
    /// <summary>
    /// Skipped (e.g., transaction was deleted or already categorized)
    /// </summary>
    Skipped = 4
}
