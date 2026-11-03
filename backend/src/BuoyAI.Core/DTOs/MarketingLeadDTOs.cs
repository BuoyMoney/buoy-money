using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.DTOs;

/// <summary>
/// Public capture payload from the marketing-site popup. Everything except the email is
/// optional so a failure to read the calculator can never block the capture.
/// </summary>
public class CaptureLeadRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    /// <summary>"popup_timed" | "popup_exit" | "calculator_exit". Unknown values are dropped.</summary>
    [MaxLength(40)]
    public string? Source { get; set; }

    /// <summary>Calculator inputs/results, when captured from the calculator prompt.</summary>
    public CalculatorSnapshotDto? Calculator { get; set; }
}

/// <summary>
/// The numbers a visitor had on screen in the daily-allowance calculator. Echoed back in
/// the confirmation email so the "save these numbers" promise is actually kept.
/// </summary>
public class CalculatorSnapshotDto
{
    public decimal MonthlyBudget { get; set; }
    public int Days { get; set; }
    public int SkipsPerWeek { get; set; }
    public decimal DailyAllowance { get; set; }
    public decimal Banked { get; set; }
    public decimal Yearly { get; set; }
}

/// <summary>Opt-out request carrying the opaque token from an email footer.</summary>
public class UnsubscribeLeadRequest
{
    [MaxLength(64)]
    public string? Token { get; set; }
}

/// <summary>
/// Response to a capture. Deliberately identical whether the address is brand new, a
/// repeat submit, or already registered: the endpoint is public, so it must not become an
/// oracle for "does this person have a BuoyAI account".
/// </summary>
public class CaptureLeadResponse
{
    /// <summary>Trial length this address will get on sign-up, in days.</summary>
    public int TrialDays { get; set; }

    /// <summary>The standard offer, for "X instead of Y" copy.</summary>
    public int StandardTrialDays { get; set; }
}
