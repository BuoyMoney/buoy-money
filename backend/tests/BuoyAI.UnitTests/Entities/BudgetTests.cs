using BuoyAI.Core.Entities;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Entities;

public class BudgetTests
{
  // CurrentPeriodEnd is stored as the last calendar day at midnight (e.g. May 31 00:00). Transactions
  // entered later that same day carry a time-of-day component (the default Add-Transaction UI, manual
  // create, and the MCP tool all send time-bearing dates). A plain `Date <= CurrentPeriodEnd` bound
  // dropped that last-day spend from budget accounting entirely; CurrentPeriodEndInclusive extends the
  // bound to the end of the last calendar day so the whole day is covered.

  [Fact]
  public void CurrentPeriodEndInclusive_CoversEntireLastCalendarDay()
  {
    var budget = new Budget
    {
      CurrentPeriodStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
      CurrentPeriodEnd = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc)
    };

    budget.CurrentPeriodEndInclusive.Should().Be(new DateTime(2026, 5, 31, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999));
  }

  [Fact]
  public void CurrentPeriodEndInclusive_IncludesLastDayTimeBearingTransaction()
  {
    var budget = new Budget
    {
      CurrentPeriodStart = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
      CurrentPeriodEnd = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc)
    };

    // Saved on May 31 at 23:15 — dropped by the old `<= CurrentPeriodEnd` (00:00) bound.
    var lastDaySpend = new DateTime(2026, 5, 31, 23, 15, 0, DateTimeKind.Utc);
    (lastDaySpend <= budget.CurrentPeriodEndInclusive).Should().BeTrue();

    // The next period's midnight start must NOT be pulled into this period.
    var nextPeriodStart = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    (nextPeriodStart <= budget.CurrentPeriodEndInclusive).Should().BeFalse();
  }
}
