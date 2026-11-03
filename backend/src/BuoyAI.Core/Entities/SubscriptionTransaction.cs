namespace BuoyAI.Core.Entities;

public class SubscriptionTransaction
{
  public int Id { get; set; }
  public int SubscriptionId { get; set; }
  public int TransactionId { get; set; }
  public DateTime CreatedAt { get; set; }

  // Navigation properties
  public Subscription Subscription { get; set; } = null!;
  public Transaction Transaction { get; set; } = null!;
}
