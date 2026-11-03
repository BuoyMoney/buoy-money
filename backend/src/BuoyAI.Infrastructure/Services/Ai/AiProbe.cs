using System.Diagnostics;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services.Ai;

public sealed record AiProbeStep(string Id, string Name, bool Ok, string Detail, long Milliseconds, int? InputTokens, int? OutputTokens);

/// <summary>
/// A short live check the settings screen runs on demand: can the server reach the model, and does
/// the model answer the categorization prompt in the format the parser needs. The second step
/// uses the real prompt and the real parser on a made-up fuel purchase (the prompt's own examples are coffee, so a model can't pass by copying one), so a pass means
/// categorization will work, not just that the network is up. Two small calls; no user data.
/// </summary>
public static class AiProbe
{
    public static async Task<IReadOnlyList<AiProbeStep>> RunAsync(IAiProvider provider, string defaultModel, CancellationToken ct = default)
    {
        var steps = new List<AiProbeStep>();
        if (!provider.IsConfigured)
        {
            steps.Add(new AiProbeStep("reach", "Reach the model", false, "AI is off or not fully set up.", 0, null, null));
            return steps;
        }

        steps.Add(await TimedAsync("reach", "Reach the model", provider, defaultModel,
            "Reply with the single word: ready", reply => reply.Trim().Length > 0 ? "The model answered." : null, ct));
        if (!steps[0].Ok) return steps;

        var prompt = AICategorizationService.BuildCategorizationPrompt(SampleTransaction(), new(), new());
        steps.Add(await TimedAsync("categorization", "Answers in the categorization format", provider, defaultModel,
            prompt, reply =>
            {
                var parsed = AICategorizationService.ParseCategorizationResponse(reply, new());
                return parsed.Confidence > 0
                    ? $"Understood: {parsed.DisplayName} ({parsed.Confidence:P0})."
                    : null;
            }, ct, failureHint: "The reply could not be read as CATEGORY_ID:CONFIDENCE:tags. Try a larger or instruction-tuned model."));
        return steps;
    }

    private static async Task<AiProbeStep> TimedAsync(
        string id, string name, IAiProvider provider, string model, string prompt,
        Func<string, string?> check, CancellationToken ct, string? failureHint = null)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await provider.GenerateTextAsync(new AiTextRequest(prompt, model, "categorization"), ct);
            watch.Stop();
            var ok = check(result.Text);
            return ok != null
                ? new AiProbeStep(id, name, true, ok, watch.ElapsedMilliseconds, result.InputTokens, result.OutputTokens)
                : new AiProbeStep(id, name, false, failureHint ?? "The reply was empty.", watch.ElapsedMilliseconds, result.InputTokens, result.OutputTokens);
        }
        catch (AiUnavailableException ex)
        {
            return new AiProbeStep(id, name, false, ex.Message, watch.ElapsedMilliseconds, null, null);
        }
    }

    private static Transaction SampleTransaction() => new()
    {
        Amount = 41.20m,
        Type = TransactionType.Debit,
        MerchantName = "Shell",
        Description = "SHELL OIL 57444123",
        Date = new DateTime(2026, 10, 3),
    };
}
