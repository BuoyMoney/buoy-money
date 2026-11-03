namespace BuoyAI.Core.DTOs.Plaid;

public enum HistoricalDataRange
{
    TwoMonths,
    OneYear,
    TwoYears,
    Custom
}

public class ImportHistoricalDataRequest
{
    public string PublicToken { get; set; } = string.Empty;
    public int UserId { get; set; }
    public HistoricalDataRange Range { get; set; } = HistoricalDataRange.TwoMonths;
    public DateTime? CustomStartDate { get; set; }
    public DateTime? CustomEndDate { get; set; }
}

public class ExchangePublicTokenWithImportRequest : ExchangePublicTokenRequest
{
    public HistoricalDataRange ImportRange { get; set; } = HistoricalDataRange.TwoMonths;
    public DateTime? CustomStartDate { get; set; }
    public DateTime? CustomEndDate { get; set; }
}
