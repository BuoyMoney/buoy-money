namespace BuoyAI.Core.DTOs.Billing;

public class PromoCodeValidationResult
{
    public bool IsValid { get; set; }
    public string PromoCode { get; set; } = string.Empty;
    public decimal DiscountPercent { get; set; }
    public long DiscountAmount { get; set; } // Amount in cents
    public long OriginalPrice { get; set; } // Amount in cents
    public long FinalPrice { get; set; } // Amount in cents
    public string Description { get; set; } = string.Empty;
}
