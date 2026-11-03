using Mscc.GenerativeAI.Types;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Low thinking, for tool-calling turns (a model left to think by default can
/// return thought-only turns with no answer or function call). Gemini 3 and later take a thinking
/// level; Google deprecated thinking_budget for them (newer models reject it with 400, notice of
/// 2026-10-06). Low is the lowest level every Gemini 3 flash model we use accepts: 3.7 and 3.8
/// flash answer "Thinking level MINIMAL is not supported" (checked 2026-10-06). The Gemini 2.5
/// fallback models only understand a budget, so they keep budget 0.
/// </summary>
public static class GeminiThinking
{
  public static ThinkingConfig Low(string model) =>
    IsBudgetOnly(model)
      ? new ThinkingConfig { ThinkingBudget = 0 }
      : new ThinkingConfig { ThinkingLevel = ThinkingLevel.Low };

  /// <summary>Gemini 2.x models predate thinking levels.</summary>
  public static bool IsBudgetOnly(string model) =>
    (model ?? string.Empty).Replace("models/", string.Empty).StartsWith("gemini-2", StringComparison.OrdinalIgnoreCase);
}
