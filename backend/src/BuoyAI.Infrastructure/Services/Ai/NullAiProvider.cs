using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// AI is off. Never configured, never calls out, never returns a made-up reply: a generate call
/// throws <see cref="AiUnavailableException"/> and callers that can degrade skip the work.
/// </summary>
public sealed class NullAiProvider : IAiProvider
{
    public const string Message = "AI is turned off on this server. An administrator can choose an AI provider in the server settings.";

    public string Id => AiProviderOptions.None;
    public bool IsConfigured => false;
    public AiCapabilities Capabilities => AiCapabilities.None;
    public string ResolveModel(string requestedModel, string feature) => requestedModel;

    public Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default) =>
        throw new AiUnavailableException(Message, null);
}

/// <summary>Embeddings with AI off (or on a provider with no embedding model): disabled, so index work is skipped.</summary>
public sealed class DisabledEmbeddingClient : IEmbeddingClient
{
    public bool IsEnabled => false;
    public string Model => "none";
    public int Dimensions => 768;
    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<float[]?>(null);
}
