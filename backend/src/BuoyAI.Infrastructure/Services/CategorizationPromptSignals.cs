using System.Globalization;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// One category the user has filed a merchant under, with the features that tell its rows
/// apart from the merchant's other categories: how often, how much, and when.
/// </summary>
public record MerchantSplit(
    string DisplayName,
    int Count,
    decimal AverageAmount,
    /// <summary>Share of rows dated on a Saturday or Sunday, 0..1.</summary>
    double WeekendShare);

/// <summary>
/// Pure helpers that turn a user's history into the context the categorization prompt needs
/// for nuance: a merchant the user files under several categories can only be split by
/// amount, day of week and position in the month, so those are made explicit rather than
/// left for the model to infer from a date string.
/// </summary>
internal static class CategorizationPromptSignals
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Merchant → learned pattern from the user's confirmed rows. The dominant (most recent)
    /// category drives the deterministic shortcut; the per-category splits feed the prompt
    /// when the merchant is not stable.
    /// </summary>
    public static Dictionary<string, CategoryPattern> BuildPatterns(IEnumerable<Transaction> transactions)
    {
        var patterns = new Dictionary<string, CategoryPattern>(StringComparer.OrdinalIgnoreCase);

        var byMerchant = transactions
            .Where(t => t.IsUserCategorized && !string.IsNullOrEmpty(t.MerchantName))
            .GroupBy(t => t.MerchantName!.ToLowerInvariant());

        foreach (var merchant in byMerchant)
        {
            var mostRecent = merchant.OrderByDescending(t => t.Date).First();
            var categoryId = UnifiedCategoryId(mostRecent);
            var isCustom = categoryId >= 1000;
            var userCategoryId = isCustom ? categoryId - 1000 : (int?)null;
            var systemCategory = isCustom ? TransactionCategory.Uncategorized : (TransactionCategory)categoryId;
            var displayName = mostRecent.CategoryNav?.Name ?? systemCategory.ToString();

            var splits = merchant
                .GroupBy(UnifiedCategoryId)
                .Select(g => new MerchantSplit(
                    DisplayName: g.Select(t => t.CategoryNav?.Name).FirstOrDefault(n => n != null)
                                 ?? (g.Key >= 1000 ? $"C{g.Key}" : ((TransactionCategory)g.Key).ToString()),
                    Count: g.Count(),
                    AverageAmount: Math.Round(g.Average(t => Math.Abs(t.Amount)), 2),
                    WeekendShare: g.Count(t => IsWeekend(t.Date)) / (double)g.Count()))
                .OrderByDescending(s => s.Count)
                .ThenBy(s => s.DisplayName)
                .ToList();

            patterns[merchant.Key] = new CategoryPattern(
                systemCategory, userCategoryId, displayName, splits.Count, splits);
        }

        return patterns;
    }

    /// <summary>
    /// "'dillons': Groceries 7x (avg $85.00, mostly weekends), Household 3x (avg $28.00, mostly weekdays)"
    /// </summary>
    public static string DescribeSplit(string merchant, CategoryPattern pattern)
    {
        var parts = pattern.Splits.Select(s =>
            $"{s.DisplayName} {s.Count}x (avg ${s.AverageAmount.ToString("F2", Invariant)}, {DescribeWeekendShare(s.WeekendShare)})");
        return $"'{merchant}': {string.Join(", ", parts)}";
    }

    /// <summary>"Wednesday, day 17 of 30 (mid-month)"</summary>
    public static string DescribeTiming(DateTime date)
    {
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        return $"{date.DayOfWeek}, day {date.Day} of {daysInMonth} ({DescribeMonthPosition(date.Day, daysInMonth)})";
    }

    /// <summary>"FOOD_AND_DRINK / GROCERIES (confidence HIGH)" or null when Plaid gave nothing.</summary>
    public static string? DescribePlaidGuess(Transaction t)
    {
        if (string.IsNullOrWhiteSpace(t.PlaidCategoryPrimary) && string.IsNullOrWhiteSpace(t.PlaidCategoryDetailed))
            return null;
        var category = string.IsNullOrWhiteSpace(t.PlaidCategoryDetailed)
            ? t.PlaidCategoryPrimary
            : string.IsNullOrWhiteSpace(t.PlaidCategoryPrimary)
                ? t.PlaidCategoryDetailed
                : $"{t.PlaidCategoryPrimary} / {t.PlaidCategoryDetailed}";
        var confidence = string.IsNullOrWhiteSpace(t.PlaidCategoryConfidence) ? "unknown" : t.PlaidCategoryConfidence;
        return $"{category} (confidence {confidence})";
    }

    internal static string DescribeWeekendShare(double share) => share switch
    {
        >= 0.75 => "mostly weekends",
        <= 0.25 => "mostly weekdays",
        _ => "any day",
    };

    internal static string DescribeMonthPosition(int day, int daysInMonth)
    {
        var third = daysInMonth / 3.0;
        if (day <= third) return "early month";
        if (day <= 2 * third) return "mid-month";
        return "late month";
    }

    private static bool IsWeekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static int UnifiedCategoryId(Transaction t) => t.CategoryId ?? (int)TransactionCategory.Uncategorized;
}
