using Microsoft.Extensions.Logging;
using Sentry;

namespace BuoyAI.Infrastructure.Telemetry;

/// <summary>
/// One place to record Gemini spend and to report the API refusing to serve us.
///
/// Gemini is called from seven different services, each holding its own API key and its
/// own HTTP or SDK call, and until now only the chat path recorded anything. Everything
/// else billed against the same key while being invisible to both AIUsageRecords and the
/// cost metric, which left the spend-rate alert blind over exactly the workloads most able
/// to run away: bulk transaction categorisation and the agentic tool loop.
///
/// Centralised rather than repeated at each call site so the token estimate, the pricing,
/// and the Sentry fingerprint are written once. Six near-identical copies is how they
/// drift apart.
/// </summary>
public sealed class GeminiUsageReporter
{
    private readonly BuoyAiMetrics _metrics;
    private readonly ILogger<GeminiUsageReporter> _logger;

    public GeminiUsageReporter(BuoyAiMetrics metrics, ILogger<GeminiUsageReporter> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    // Priced from the shared table (AiPricing), the same one the usage rows and the chat service use. Embeddings are
    // the one flat rate, because they are not in the generation table.
    private const decimal EmbeddingPer1k = 0.00002m;

    /// <summary>
    /// Records a text generation call.
    ///
    /// Takes the token counts rather than the SDK's response type on purpose: this class
    /// stays free of a Mscc.GenerativeAI dependency, and callers on the REST paths can use
    /// it just as easily as the SDK ones.
    ///
    /// Pass <c>response?.UsageMetadata?.PromptTokenCount</c> and
    /// <c>?.CandidatesTokenCount</c> when the response carries them. They are what Google
    /// actually billed and they account for prompt caching, which a character count cannot
    /// see and would badly over-report. The character fallback is only for when they are
    /// absent.
    /// </summary>
    public void RecordGeneration(
        string model,
        string context,
        int? reportedInputTokens,
        int? reportedOutputTokens,
        int promptChars = 0,
        int responseChars = 0)
    {
        var inputTokens = reportedInputTokens ?? EstimateTokens(promptChars);
        var outputTokens = reportedOutputTokens ?? EstimateTokens(responseChars);
        RecordGenerationCore(model, context, inputTokens, outputTokens);
    }

    private void RecordGenerationCore(string model, string context, int inputTokens, int outputTokens)
    {
        var cost = BuoyAI.Infrastructure.Services.Ai.AiPricing.Cost(model, inputTokens, outputTokens, _logger);
        _metrics.RecordAiUsage(model, context, inputTokens + outputTokens, cost);
    }

    /// <summary>
    /// Records an embedding call. Embeddings are input-only, so there is no output side.
    /// </summary>
    public void RecordEmbedding(string model, string context, int inputTokens)
    {
        var cost = (inputTokens / 1000m) * EmbeddingPer1k;
        _metrics.RecordAiUsage(model, context, inputTokens, cost);
    }

    /// <summary>~4 characters per token, the usual rough conversion.</summary>
    public static int EstimateTokens(int characters) => Math.Max(1, characters / 4);

    /// <summary>
    /// Reports Gemini refusing the request because of quota or billing.
    ///
    /// 429 is quota exhausted, 403 is a rejected key or disabled billing. Under prepaid
    /// credits these are the shapes "the balance ran out" arrives in. The spend-rate alert
    /// catches a runaway BEFORE the credits are gone; this catches the case where they
    /// already are, which matters more now that auto-top-up removed the natural stop at
    /// zero and only the monthly cap bounds it.
    ///
    /// Fingerprinted on status alone so every occurrence across every surface groups into
    /// one actionable issue rather than scattering by response body.
    /// </summary>
    public void ReportRefusal(int status, string surface, string model, string? body = null)
    {
        if (status != 429 && status != 403) return;

        _logger.LogError(
            "Gemini refused {Surface} with {Status} (model {Model}) — check prepaid balance",
            surface, status, model);

        SentrySdk.CaptureMessage(
            $"Gemini refused with {status} — check prepaid balance",
            scope =>
            {
                scope.SetTag("gemini.status", status.ToString());
                scope.SetTag("gemini.surface", surface);
                scope.SetTag("gemini.model", model);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    // Truncated: the body can carry a long error payload, and the useful
                    // part is always at the front.
                    scope.SetExtra("response", body.Length > 500 ? body[..500] : body);
                }
                scope.SetFingerprint(new[] { "gemini-quota", status.ToString() });
            },
            SentryLevel.Error);
    }
}
