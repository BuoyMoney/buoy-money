namespace BuoyAI.Core.DTOs.Transaction;

/// <summary>One leg of a suspected transfer.</summary>
public class SuspectedTransferSideDto
{
    public int TransactionId { get; set; }
    public int AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
    public string MerchantName { get; set; } = string.Empty;
}

/// <summary>
/// A transaction (or matched pair) that looks like an internal transfer but is counted as spending.
/// Surfaced for the user to confirm — the app never reclassifies on its own.
/// </summary>
public class SuspectedTransferDto
{
    public string Confidence { get; set; } = "low"; // "low" | "medium" | "high"
    public string Reason { get; set; } = string.Empty;
    public List<SuspectedTransferSideDto> Sides { get; set; } = new();
}

/// <summary>Confirm one or more legs as an actual transfer (recategorize to Transfer).</summary>
public class ConfirmTransferRequest
{
    public List<int> TransactionIds { get; set; } = new();
}

/// <summary>Dismiss a suspected transfer: mark the legs reviewed so they stop being suggested.</summary>
public class DismissTransferRequest
{
    public List<int> TransactionIds { get; set; } = new();
}
