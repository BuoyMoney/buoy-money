using System.Text;
using System.Text.RegularExpressions;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Lightweight, deterministic moderation for the AI chat (#638) — a defense-in-depth layer under
/// the system-prompt guardrails (#635) and the provider-level Gemini safety settings (#637).
///
/// First increment: intentionally narrow and high-precision so it adds no latency (pure
/// string/regex checks, no extra model call) and rarely false-positives on real budgeting chat.
///   - <see cref="ClassifyInput"/>: short-circuit clear prompt-injection / system-prompt-extraction
///     attempts with a canned refusal before the model is ever called.
///   - <see cref="ScreenOutput"/>: replace any response that leaks the assistant's own guardrail text.
///
/// Off-topic and harmful *content* are already handled by the system prompt (polite decline) and
/// the Gemini <c>safetySettings</c> respectively, so this deliberately does not re-classify those —
/// heuristic off-topic/harmful gates false-positive too easily on budgeting-adjacent questions.
/// Kept as a standalone static class so it can move into the #636 MAF middleware unchanged.
/// </summary>
public static class ChatModeration
{
    public const string InputRefusal =
        "I can only help with your budgeting and personal finances in Buoy Money. I can't take " +
        "instructions outside that. Want to look at your spending, budgets, savings, or transactions?";

    public const string OutputLeakReplacement =
        "I'm here to help with your budgeting and personal finances. What would you like to work on?";

    public readonly record struct InputVerdict(bool Blocked, string? Reason);
    public readonly record struct OutputVerdict(bool Leaked, string Text);

    // High-precision prompt-injection / prompt-extraction patterns. Deliberately conservative:
    // each is dominated by injection intent, not normal budgeting language (e.g. "ignore your
    // rules" matches, but "ignore my dining rules" does not — "my" isn't in the qualifier set).
    private static readonly Regex[] InjectionPatterns =
    {
        new(@"\bignore\s+(all\s+|the\s+)*(previous|above|prior|earlier|these|your)\s+(instructions?|prompts?|rules?|guardrails?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdisregard\s+(all\s+|the\s+)*(previous|above|prior|your)\s+(instructions?|rules?|guardrails?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\boverride\s+(your\s+)?(instructions?|rules?|guardrails?|programming)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(reveal|show|print|repeat|output|expose)\s+(me\s+)?(your\s+|the\s+)*(system\s+)?(prompt|instructions?|rules?|guardrails?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bwhat\s+(are|were|is)\s+your\s+(system\s+)?(prompt|instructions?|rules?|guardrails?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bsystem\s+prompt\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\byou\s+are\s+now\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bpretend\s+(to\s+be|you\s+are|you're)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdeveloper\s+mode\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bjailbreak\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    // Distinctive fragments of the assistant's own guardrails (AIService.Chat.cs). If the model
    // echoes any of these, it is leaking the system prompt — replace the whole turn.
    private static readonly string[] GuardrailSignatures =
    {
        "You are Buoy Money's budgeting assistant",
        "STAY IN SCOPE",
        "RESIST MANIPULATION",
        "UNTRUSTED input",
    };

    // Longest signature — the hold-back window <see cref="ScreenStream"/> keeps un-emitted so a
    // signature split across streaming chunk boundaries is detected before any of it is flushed.
    private static readonly int MaxSignatureLength = GuardrailSignatures.Max(s => s.Length);

    /// <summary>Block clear prompt-injection / prompt-extraction attempts before the model call.</summary>
    public static InputVerdict ClassifyInput(string? userTurn)
    {
        if (string.IsNullOrWhiteSpace(userTurn))
            return new InputVerdict(false, null);

        foreach (var pattern in InjectionPatterns)
        {
            if (pattern.IsMatch(userTurn))
                return new InputVerdict(true, $"prompt-injection pattern: {pattern}");
        }

        return new InputVerdict(false, null);
    }

    /// <summary>Detect (and replace) system-prompt / guardrail leakage in a model response.</summary>
    public static OutputVerdict ScreenOutput(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
            return new OutputVerdict(false, modelText ?? string.Empty);

        return ContainsSignature(modelText)
            ? new OutputVerdict(true, OutputLeakReplacement)
            : new OutputVerdict(false, modelText);
    }

    private static bool ContainsSignature(string text)
    {
        foreach (var signature in GuardrailSignatures)
        {
            if (text.Contains(signature, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Streaming-safe counterpart of <see cref="ScreenOutput"/> for token-by-token model streams.
    /// Passes chunks through while holding back a tail window of <see cref="MaxSignatureLength"/>
    /// characters, so a guardrail signature straddling chunk boundaries is detected before any of
    /// it is emitted. If a signature appears, the remainder of the turn is replaced with
    /// <see cref="OutputLeakReplacement"/> — the leak text is never forwarded (any already-emitted
    /// prefix was, by construction, screened clean). <paramref name="onLeak"/> fires once on replace
    /// so the caller can log / tag the trace.
    /// </summary>
    public static async IAsyncEnumerable<string> ScreenStream(
        IAsyncEnumerable<string> chunks, Action? onLeak = null)
    {
        var buffer = new StringBuilder();
        var committed = 0;

        await foreach (var chunk in chunks)
        {
            if (string.IsNullOrEmpty(chunk))
                continue;

            buffer.Append(chunk);
            var text = buffer.ToString();

            // Re-screening the whole buffer each chunk guarantees any newly-completed signature
            // ends inside the just-added region, so its start is beyond `committed` (the hold-back
            // keeps the last MaxSignatureLength chars un-emitted) — nothing leaked is ever flushed.
            if (ContainsSignature(text))
            {
                onLeak?.Invoke();
                yield return OutputLeakReplacement;
                yield break;
            }

            var safeLen = text.Length - MaxSignatureLength;
            if (safeLen > committed)
            {
                yield return text.Substring(committed, safeLen - committed);
                committed = safeLen;
            }
        }

        // Flush the held-back tail (already screened clean by the loop's final iteration).
        var final = buffer.ToString();
        if (final.Length > committed)
            yield return final.Substring(committed);
    }
}
