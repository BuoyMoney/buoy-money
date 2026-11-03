using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class SubscriptionMerchantCategoriesTests
{
  [Theory]
  [InlineData("MATTRESS FIRM")]
  [InlineData("CASINO VEGAS")]
  [InlineData("COXSWAIN SUPPLY")]
  [InlineData("MAXWELL HOUSE CAFE")]
  public void Guess_DoesNotMatchKeywordsInsideOtherWords(string merchant)
  {
    SubscriptionMerchantCategories.Guess(merchant).Should().Be(SubscriptionCategory.Other);
  }

  [Theory]
  [InlineData("NETFLIX.COM", SubscriptionCategory.Streaming)]
  [InlineData("SPOTIFY USA", SubscriptionCategory.Streaming)]
  [InlineData("GOOGLE*YOUTUBE", SubscriptionCategory.Streaming)]
  [InlineData("ZOOM.US", SubscriptionCategory.Software)]
  [InlineData("ATT BILL PAYMENT", SubscriptionCategory.Utilities)]
  [InlineData("AT&T WIRELESS", SubscriptionCategory.Utilities)]
  [InlineData("COX COMMUNICATIONS", SubscriptionCategory.Utilities)]
  [InlineData("PLANET FITNESS", SubscriptionCategory.Fitness)]
  [InlineData("GEICO AUTO", SubscriptionCategory.Insurance)]
  public void Guess_MatchesWholeWordKeywords(string merchant, SubscriptionCategory expected)
  {
    SubscriptionMerchantCategories.Guess(merchant).Should().Be(expected);
  }
}
