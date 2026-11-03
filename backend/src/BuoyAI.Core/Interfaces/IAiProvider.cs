namespace BuoyAI.Core.Interfaces;

/// <summary>
/// One place that sends a prompt to a language model and gets text back. Which model service sits
/// behind it (Gemini, an OpenAI-compatible server such as Ollama, or none) is a startup setting.
/// Features call this instead of building their own client, so a self-hosted server can run its
/// categorization on a local model and a server with AI off never reaches out at all.
/// </summary>
/// <remarks>
/// Text in, text out, with the token counts the provider reported. Tool calling, streaming and
/// file uploads are not on this interface yet: they are still Gemini-only code paths, and
/// <see cref="AiCapabilities"/> says so, so the UI can hide what a provider can't do.
/// </remarks>
public interface IAiProvider
{
    /// <summary>"gemini", "none", or the preset name ("ollama", "openai", "openrouter", "gemini-compat", "custom").</summary>
    string Id { get; }

    /// <summary>
    /// False when AI is off or the provider isn't fully set up (no key, no model). Callers skip AI
    /// work instead of failing; a generate call on an unconfigured provider throws
    /// <see cref="BuoyAI.Core.Exceptions.AiUnavailableException"/>.
    /// </summary>
    bool IsConfigured { get; }

    AiCapabilities Capabilities { get; }

    /// <summary>
    /// The model to send for <paramref name="feature"/>. Gemini uses the model the caller picked
    /// (the tier map, hosted behaviour unchanged); other providers use the operator's configured
    /// model for the feature, because Gemini model names mean nothing to them.
    /// </summary>
    string ResolveModel(string requestedModel, string feature);

    /// <exception cref="BuoyAI.Core.Exceptions.AiUnavailableException">AI is off, not set up, or the provider is unreachable.</exception>
    Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Prompt">The whole prompt, sent as one user message.</param>
/// <param name="Model">The model the caller wants. Providers other than Gemini replace it via <see cref="IAiProvider.ResolveModel"/>.</param>
/// <param name="Feature">Closed-ish set of names ("categorization", "insights", "memory-extraction", "subscriptions"). Used for usage metrics and per-feature model overrides.</param>
/// <param name="Files">Images or PDFs sent with the prompt. Only providers whose <see cref="AiCapabilities.Attachments"/> is true accept them.</param>
public sealed record AiTextRequest(
    string Prompt,
    string Model,
    string Feature,
    IReadOnlyList<(string MimeType, byte[] Data)>? Files = null);

/// <param name="Text">The reply as the provider sent it (callers trim if they need to).</param>
/// <param name="InputTokens">What the provider reported, or null when it reported nothing.</param>
/// <param name="CostUsd">What the call cost when the provider knows it (local models: 0, OpenRouter: its own figure). Null for Gemini, which prices by the model table.</param>
public sealed record AiTextResult(string Text, int? InputTokens, int? OutputTokens, decimal? CostUsd = null);

/// <summary>What a provider can do today, as the code stands (not what the service could do in theory).</summary>
public sealed record AiCapabilities(
    bool Text,
    bool ChatAndAgents,
    bool Attachments,
    bool Embeddings)
{
    public static readonly AiCapabilities None = new(false, false, false, false);
}
