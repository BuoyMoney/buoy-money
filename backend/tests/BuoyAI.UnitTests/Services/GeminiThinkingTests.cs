using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Mscc.GenerativeAI.Types;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Google deprecated thinking_budget (newer models answer 400) in favour of thinking_level, which
/// the Gemini 2.5 fallback doesn't understand. Each model gets the setting it accepts.
/// </summary>
public class GeminiThinkingTests
{
  [Theory]
  [InlineData("gemini-3.8-flash")]
  [InlineData("gemini-3.7-flash")]
  [InlineData("gemini-3.5-flash-lite")]
  [InlineData("models/gemini-3.8-flash")]
  public void Low_Gemini3AndLater_UsesThinkingLevelNotBudget(string model)
  {
    var config = GeminiThinking.Low(model);

    config.ThinkingLevel.Should().Be(ThinkingLevel.Low);
    config.ThinkingBudget.Should().BeNull();
  }

  [Theory]
  [InlineData("gemini-2.5-flash")]
  [InlineData("models/gemini-2.5-flash")]
  public void Low_Gemini25Fallback_KeepsBudgetZero(string model)
  {
    var config = GeminiThinking.Low(model);

    config.ThinkingBudget.Should().Be(0);
    config.ThinkingLevel.Should().BeNull();
  }
}
