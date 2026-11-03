using System;
using BuoyAI.Core.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class MonetaryLimitsTests
{
  // ── ValidateNonNegative (budget allocations: zero allowed) ──
  [Theory]
  [InlineData(0)]
  [InlineData(1)]
  [InlineData(1_000_000_000)] // exactly the cap
  public void ValidateNonNegative_WithinRange_DoesNotThrow(decimal amount)
  {
    var act = () => MonetaryLimits.ValidateNonNegative(amount, "Amount");
    act.Should().NotThrow();
  }

  [Theory]
  [InlineData(-0.01)]
  [InlineData(-100)]
  [InlineData(1_000_000_000.01)]
  [InlineData(999_999_999_999.99)] // ~$1T
  public void ValidateNonNegative_OutOfRange_Throws(decimal amount)
  {
    var act = () => MonetaryLimits.ValidateNonNegative(amount, "Amount");
    act.Should().Throw<ArgumentException>();
  }

  // ── ValidatePositive (savings target / subscription price: > 0 required) ──
  [Theory]
  [InlineData(0.01)]
  [InlineData(1_000_000_000)]
  public void ValidatePositive_WithinRange_DoesNotThrow(decimal amount)
  {
    var act = () => MonetaryLimits.ValidatePositive(amount, "Amount");
    act.Should().NotThrow();
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  [InlineData(1_000_000_000.01)]
  public void ValidatePositive_OutOfRange_Throws(decimal amount)
  {
    var act = () => MonetaryLimits.ValidatePositive(amount, "Amount");
    act.Should().Throw<ArgumentException>();
  }

  // ── ValidateSignedNonZero (manual transactions: sign meaningful) ──
  [Theory]
  [InlineData(-12.50)]
  [InlineData(12.50)]
  [InlineData(-1_000_000_000)]
  [InlineData(1_000_000_000)]
  public void ValidateSignedNonZero_WithinRange_DoesNotThrow(decimal amount)
  {
    var act = () => MonetaryLimits.ValidateSignedNonZero(amount, "Amount");
    act.Should().NotThrow();
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-999_999_999_999.99)]
  [InlineData(1_000_000_000.01)]
  public void ValidateSignedNonZero_OutOfRange_Throws(decimal amount)
  {
    var act = () => MonetaryLimits.ValidateSignedNonZero(amount, "Amount");
    act.Should().Throw<ArgumentException>();
  }
}
