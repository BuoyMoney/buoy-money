using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// AI bought from BuoyAI Cloud: each call goes to the metered gateway with this server's pairing credential. The gateway
/// picks the model, counts the cost against the allowance, and refuses when the money is gone, so a self-hosted server
/// needs no provider account of its own. Text only, like the other non-Gemini providers: chat with tools, agents and
/// attachments still run on Gemini's own code.
/// </summary>
public sealed class CloudAiProvider : IAiProvider
{
    private readonly AiProviderOptions _options;
    private readonly CloudAiGatewayClient _gateway;

    public CloudAiProvider(AiProviderOptions options, CloudAiGatewayClient gateway)
    {
        _options = options;
        _gateway = gateway;
    }

    public string Id => AiProviderOptions.BuoyCloud;
    public bool IsConfigured => _options.SetupProblem == null;

    public AiCapabilities Capabilities => new(Text: IsConfigured, ChatAndAgents: false, Attachments: false, Embeddings: false);

    // The gateway chooses the model; this name only labels the usage row until the gateway's reply says more.
    public string ResolveModel(string requestedModel, string feature) => "buoy-cloud";

    public async Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new AiUnavailableException($"BuoyAI Cloud AI isn't set up: {_options.SetupProblem}", null);
        if (request.Files is { Count: > 0 })
            throw new AiUnavailableException("BuoyAI Cloud AI can't read attachments yet. Photos and PDFs need Gemini.", null);

        var reply = await _gateway.CompleteAsync(request.Prompt, request.Feature, cancellationToken);
        // The gateway reports what its meter charged, in micro-dollars; the usage row stores dollars.
        return new AiTextResult(reply.Text, reply.InputTokens, reply.OutputTokens, reply.CallCostMicros / 1_000_000m);
    }
}
