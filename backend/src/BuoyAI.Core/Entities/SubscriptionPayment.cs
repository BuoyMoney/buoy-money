using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a payment for a user's BuoyAI subscription
/// </summary>
public class SubscriptionPayment
{
    public int Id { get; set; }
    public int UserSubscriptionId { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public string? StripeInvoiceId { get; set; }
    public DateTime PaymentDate { get; set; }
    public DateTime? FailureDate { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation properties
    public UserSubscription UserSubscription { get; set; } = null!;
}