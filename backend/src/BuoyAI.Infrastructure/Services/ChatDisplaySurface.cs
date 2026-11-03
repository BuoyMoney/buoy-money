namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// What the chat answer will be read on. A phone shows answers in a bubble about forty
/// characters wide, where a markdown table with more than two columns collapses to one
/// character per line, so the model is told to give breakdowns as short lines instead.
/// The client type comes from the <c>X-Client-Type</c> request header (see
/// <c>AppConfig.clientType</c> in the app: "web", "mobile-ios", "mobile-android", ...).
/// </summary>
internal static class ChatDisplaySurface
{
  public static bool IsCompact(string? clientType) =>
    !string.IsNullOrWhiteSpace(clientType)
    && clientType.StartsWith("mobile-", StringComparison.OrdinalIgnoreCase);

  /// <summary>The prompt section describing the display and its formatting rules.</summary>
  public static string Section(bool compact)
  {
    if (compact)
    {
      return string.Join(Environment.NewLine,
        "=== DISPLAY: PHONE ===",
        "The user reads your answer on a phone, in a chat bubble about 40 characters wide.",
        "- NEVER use markdown tables. Give any breakdown or comparison as one short line per item,",
        "  for example \"Groceries: $249 of $700 (36%)\" or \"Dining Out: $389 left\".",
        "- Prefer short paragraphs and lists over headings; at most one heading level.",
        "- Keep numbers on the same line as their label so nothing wraps mid-figure.",
        "");
    }
    return string.Join(Environment.NewLine,
      "=== DISPLAY: WIDE SCREEN ===",
      "The user reads your answer on a wide screen. Markdown tables with up to four columns",
      "are fine for breakdowns; wider than that, use one line per item instead.",
      "");
  }
}
