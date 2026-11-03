namespace BuoyAI.Core.Services;

/// <summary>
/// Shared bounds for user-entered monetary amounts (budget categories, savings
/// goals, subscriptions, manual transactions). Prevents nonsensical values —
/// negatives where invalid, and absurdly huge amounts that corrupt budget math /
/// summaries or overflow. Flagged by the #615 QE pass (Part E): several
/// user-entry surfaces had no server-side amount bound.
/// </summary>
public static class MonetaryLimits
{
  /// <summary>Maximum magnitude for any user-entered amount ($1B). Matches the
  /// marketing calculator cap; real personal-finance amounts never approach it.</summary>
  public const decimal MaxAmount = 1_000_000_000m;

  /// <summary>Throws when <paramref name="amount"/> is negative or exceeds the cap.
  /// Zero is allowed (e.g. a $0 budget category).</summary>
  public static void ValidateNonNegative(decimal amount, string field)
  {
    if (amount < 0)
      throw new ArgumentException($"{field} cannot be negative.");
    if (amount > MaxAmount)
      throw new ArgumentException($"{field} must not exceed {MaxAmount:N0}.");
  }

  /// <summary>Throws when <paramref name="amount"/> is zero/negative or exceeds the
  /// cap. Use where a positive value is required (savings target, subscription price).</summary>
  public static void ValidatePositive(decimal amount, string field)
  {
    if (amount <= 0)
      throw new ArgumentException($"{field} must be greater than zero.");
    if (amount > MaxAmount)
      throw new ArgumentException($"{field} must not exceed {MaxAmount:N0}.");
  }

  /// <summary>Throws when <paramref name="amount"/> is zero or its magnitude exceeds
  /// the cap. Use for signed amounts where the sign is meaningful (a manual
  /// transaction: negative = expense, positive = income).</summary>
  public static void ValidateSignedNonZero(decimal amount, string field)
  {
    if (amount == 0)
      throw new ArgumentException($"{field} cannot be zero.");
    if (Math.Abs(amount) > MaxAmount)
      throw new ArgumentException($"{field} must be between -{MaxAmount:N0} and {MaxAmount:N0}.");
  }
}
