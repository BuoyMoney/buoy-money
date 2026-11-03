using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Computes the Wrapped fact sheet: a wide set of candidate stats we would
/// never surface as core cards, fully preformatted server-side. The bonus-card
/// layer (deterministic rotation or AI garnish) PICKS from these; nothing
/// downstream ever computes or invents a number.
/// </summary>
public static class WrappedFactSheet
{
  // Amounts are quoted in the user's primary currency (#941). The old en-US pin
  // hard-coded "$" for every user; these thread the caller-supplied ISO code.
  private static string Money(decimal v, string? currency) => CurrencyCodes.Format(v, currency, 0);
  private static string MoneyCents(decimal v, string? currency) => CurrencyCodes.Format(v, currency);

  /// <param name="expenses">Budgetable expenses for the period.</param>
  /// <param name="prevExpenses">Budgetable expenses for the comparison period (may be empty).</param>
  /// <param name="priorMerchants">Merchant names seen in the 6 months before the period.</param>
  /// <param name="spendDays">Distinct dates that had budgetable spend.</param>
  public static List<WrappedFactDto> Build(
    List<Transaction> expenses,
    List<Transaction> prevExpenses,
    HashSet<string> priorMerchants,
    HashSet<DateTime> spendDays,
    DateTime periodStart,
    DateTime lastCountedDay,
    string? currency = null)
  {
    // Local shorthands so every quoted amount picks up the user's currency.
    string Money(decimal v) => WrappedFactSheet.Money(v, currency);
    string MoneyCents(decimal v) => WrappedFactSheet.MoneyCents(v, currency);

    var facts = new List<WrappedFactDto>();
    if (expenses.Count == 0) return facts;

    var total = expenses.Sum(t => Math.Abs(t.Amount));
    if (total <= 0) return facts;

    // weekend_share — how much of the month happened on Sat/Sun.
    var weekend = expenses
      .Where(t => t.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
      .Sum(t => Math.Abs(t.Amount));
    var weekendShare = weekend / total;
    facts.Add(new WrappedFactDto
    {
      Key = "weekend_share",
      Kicker = "weekend energy",
      Headline = "of your spending happened on weekends",
      Value = $"{weekendShare * 100:0}%",
      Detail = $"{Money(weekend)} across Saturdays and Sundays",
      // 2/7 of days ≈ 29% is neutral; distance from that is interesting.
      Surprise = Math.Clamp(Math.Abs((double)weekendShare - 0.29) * 2.4, 0, 1),
    });

    // priciest_weekday — the day of week that costs the most.
    var byWeekday = expenses
      .GroupBy(t => t.Date.DayOfWeek)
      .Select(g => (Day: g.Key, Total: g.Sum(t => Math.Abs(t.Amount))))
      .OrderByDescending(x => x.Total)
      .ToList();
    if (byWeekday.Count > 1)
    {
      var top = byWeekday[0];
      var share = top.Total / total;
      facts.Add(new WrappedFactDto
      {
        Key = "priciest_weekday",
        Kicker = "the expensive day",
        Headline = $"{top.Day}s are your spending day",
        Value = Money(top.Total),
        Detail = $"{share * 100:0}% of the period landed on {top.Day}s",
        Surprise = Math.Clamp(((double)share - 1.0 / 7) * 3.5, 0, 1),
      });
    }

    // new_merchants — places tried for the first time.
    var newMerchants = expenses
      .Where(t => !string.IsNullOrWhiteSpace(t.MerchantName)
          && !priorMerchants.Contains(t.MerchantName))
      .Select(t => t.MerchantName)
      .Distinct()
      .ToList();
    if (newMerchants.Count > 0)
    {
      facts.Add(new WrappedFactDto
      {
        Key = "new_merchants",
        Kicker = "explorer",
        Headline = newMerchants.Count == 1
            ? "new place made your statement"
            : "new places made your statement",
        Value = $"{newMerchants.Count}",
        Detail = $"Including {string.Join(", ", newMerchants.Take(3))}",
        Surprise = Math.Clamp(newMerchants.Count / 8.0, 0.2, 1),
      });
    }

    // largest_purchase — the single biggest swing.
    var largest = expenses.OrderByDescending(t => Math.Abs(t.Amount)).First();
    var avg = total / expenses.Count;
    facts.Add(new WrappedFactDto
    {
      Key = "largest_purchase",
      Kicker = "the big one",
      Headline = string.IsNullOrWhiteSpace(largest.MerchantName)
          ? "was your single biggest purchase"
          : $"went to {largest.MerchantName} in one go",
      Value = MoneyCents(Math.Abs(largest.Amount)),
      Detail = $"That's {(avg > 0 ? Math.Abs(largest.Amount) / avg : 1):0}x your typical transaction",
      Surprise = Math.Clamp(avg > 0 ? (double)(Math.Abs(largest.Amount) / avg) / 12.0 : 0, 0, 1),
    });

    // small_buys — death by a thousand cuts.
    var small = expenses.Where(t => Math.Abs(t.Amount) < 10).ToList();
    if (small.Count >= 5)
    {
      facts.Add(new WrappedFactDto
      {
        Key = "small_buys",
        Kicker = "little things",
        Headline = "purchases under $10",
        Value = $"{small.Count}",
        Detail = $"They add up to {Money(small.Sum(t => Math.Abs(t.Amount)))}",
        Surprise = Math.Clamp(small.Count / 30.0, 0.15, 1),
      });
    }

    // busiest_day — most transactions in one day.
    var busiest = expenses
      .GroupBy(t => t.Date.Date)
      .Select(g => (Day: g.Key, Count: g.Count(), Total: g.Sum(t => Math.Abs(t.Amount))))
      .OrderByDescending(x => x.Count)
      .First();
    if (busiest.Count >= 3)
    {
      facts.Add(new WrappedFactDto
      {
        Key = "busiest_day",
        Kicker = "whirlwind day",
        Headline = $"transactions on {busiest.Day:MMMM d} alone",
        Value = $"{busiest.Count}",
        Detail = $"{Money(busiest.Total)} in a single day",
        Surprise = Math.Clamp(busiest.Count / 10.0, 0.1, 1),
      });
    }

    // longest_quiet_gap — most consecutive zero-spend days.
    var gap = LongestQuietGap(spendDays, periodStart, lastCountedDay);
    if (gap >= 3)
    {
      facts.Add(new WrappedFactDto
      {
        Key = "longest_quiet_gap",
        Kicker = "still waters",
        Headline = "days in a row without spending a cent",
        Value = $"{gap}",
        Detail = "Your longest quiet stretch of the period",
        Surprise = Math.Clamp(gap / 10.0, 0.2, 1),
      });
    }

    // avg_txn + spend_per_active_day — texture stats.
    facts.Add(new WrappedFactDto
    {
      Key = "avg_txn",
      Kicker = "your typical swipe",
      Headline = "was your average transaction",
      Value = MoneyCents(avg),
      Detail = $"Across {expenses.Count} purchases",
      Surprise = 0.15,
    });
    if (spendDays.Count > 0)
    {
      facts.Add(new WrappedFactDto
      {
        Key = "spend_per_active_day",
        Kicker = "on days you spent",
        Headline = "left your accounts per spending day",
        Value = Money(total / spendDays.Count),
        Detail = $"You spent money on {spendDays.Count} days",
        Surprise = 0.2,
      });
    }

    // subscription_share.
    var subs = expenses
      .Where(t => t.CategoryId == (int)TransactionCategory.Subscriptions)
      .Sum(t => Math.Abs(t.Amount));
    if (subs > 0)
    {
      facts.Add(new WrappedFactDto
      {
        Key = "subscription_share",
        Kicker = "on autopilot",
        Headline = "of your spending was subscriptions",
        Value = $"{subs / total * 100:0}%",
        Detail = $"{Money(subs)} charged without a decision",
        Surprise = Math.Clamp((double)(subs / total) * 3, 0.1, 1),
      });
    }

    // category_move — biggest change vs the previous period.
    if (prevExpenses.Count > 0)
    {
      var prevByCat = prevExpenses
        .GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized)
        .ToDictionary(g => g.Key, g => g.Sum(t => Math.Abs(t.Amount)));
      var move = expenses
        .GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized)
        .Select(g =>
        {
          var now = g.Sum(t => Math.Abs(t.Amount));
          var before = prevByCat.GetValueOrDefault(g.Key);
          return (CategoryId: g.Key,
              Name: g.First().CategoryNav?.Name ?? ((TransactionCategory)g.Key).ToString(),
              Delta: now - before, Before: before, Now: now);
        })
        .Where(x => x.Before > 25 || x.Now > 25)
        .OrderByDescending(x => Math.Abs(x.Delta))
        .FirstOrDefault();
      if (move.Name != null && Math.Abs(move.Delta) > 25)
      {
        var fell = move.Delta < 0;
        facts.Add(new WrappedFactDto
        {
          Key = "category_move",
          Kicker = fell ? "trending down" : "trending up",
          Headline = fell
              ? $"less on {move.Name} than last period"
              : $"more on {move.Name} than last period",
          Value = Money(Math.Abs(move.Delta)),
          Detail = $"{Money(move.Before)} then, {Money(move.Now)} now",
          Surprise = Math.Clamp((double)(Math.Abs(move.Delta) / Math.Max(total, 1)) * 4, 0.25, 1),
        });
      }
    }

    return facts;
  }

  private static int LongestQuietGap(
    HashSet<DateTime> spendDays, DateTime periodStart, DateTime lastCountedDay)
  {
    var best = 0;
    var run = 0;
    for (var day = periodStart.Date; day <= lastCountedDay; day = day.AddDays(1))
    {
      if (spendDays.Contains(day))
      {
        run = 0;
      }
      else
      {
        run++;
        if (run > best) best = run;
      }
    }
    return best;
  }
}
