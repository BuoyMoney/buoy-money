using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// Wraps the configured provider so every call made on a user's behalf (see <see cref="AiUsageScope"/>) writes one usage row
/// with the provider's own token counts and the cost of the model actually used. This is the single place that closes the
/// "only chat and categorization were recorded" gap for the text paths: insights, receipts, recap wording, memory
/// extraction and subscription wording all go through here and nowhere else needs to remember to record.
///
/// A call with no user in scope writes nothing (broadcasts, benchmarks). Categorization sets no scope because it records
/// its own row. A failed call writes nothing (it cost nothing, or the provider's meter has it). A recording failure is
/// logged and never fails the call.
/// </summary>
public sealed class MeteredAiProvider : IAiProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MeteredAiProvider> _logger;

    public MeteredAiProvider(IAiProvider inner, IServiceScopeFactory scopes, ILogger<MeteredAiProvider> logger)
    {
        Inner = inner;
        _scopes = scopes;
        _logger = logger;
    }

    public IAiProvider Inner { get; }

    public string Id => Inner.Id;
    public bool IsConfigured => Inner.IsConfigured;
    public AiCapabilities Capabilities => Inner.Capabilities;
    public string ResolveModel(string requestedModel, string feature) => Inner.ResolveModel(requestedModel, feature);

    public async Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default)
    {
        var userId = AiUsageScope.CurrentUserId;
        var result = await Inner.GenerateTextAsync(request, cancellationToken);
        if (userId is not { } id) return result;

        try
        {
            var model = Inner.ResolveModel(request.Model, request.Feature);
            var estimated = result.InputTokens == null || result.OutputTokens == null;
            // No counts from the provider: ~4 characters a token, and the row says it is a guess.
            var input = result.InputTokens ?? Math.Max(1, request.Prompt.Length / 4);
            var output = result.OutputTokens ?? Math.Max(1, result.Text.Length / 4);
            var cost = result.CostUsd ?? AiPricing.Cost(model, input, output, _logger);

            using var scope = _scopes.CreateScope();
            var tracker = scope.ServiceProvider.GetRequiredService<IAIUsageTracker>();
            // The providers already feed the spend metrics themselves, so the row doesn't emit them a second time.
            await tracker.RecordCallAsync(new AiUsageEntry(id, model, Inner.Id, input, output, estimated, cost, request.Feature, EmitMetrics: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record AI usage for user {UserId} ({Feature})", id, request.Feature);
        }
        return result;
    }
}
