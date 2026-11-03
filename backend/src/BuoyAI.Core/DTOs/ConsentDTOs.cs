namespace BuoyAI.Core.DTOs;

// Consent Management DTOs
public class RecordConsentRequest
{
    public string ConsentType { get; set; } = null!;
    public string ConsentVersion { get; set; } = null!;
    public bool HasConsented { get; set; }
    public string? ConsentText { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

public class ConsentHistoryResponse
{
    public int Id { get; set; }
    public string ConsentType { get; set; } = null!;
    public string ConsentVersion { get; set; } = null!;
    public bool HasConsented { get; set; }
    public DateTime ConsentedAt { get; set; }
    public string? ConsentText { get; set; }
}

public class PlaidConsentData
{
    public bool DataSharing { get; set; }
    public bool TransactionAccess { get; set; }
    public bool AccountAccess { get; set; }
    public bool BalanceAccess { get; set; }
    public bool IdentityAccess { get; set; }
    public bool TermsAccepted { get; set; }
    public bool PrivacyPolicyAccepted { get; set; }
}
