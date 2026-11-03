using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.Ai;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The text-generation interface that insights, Wrapped copy and receipt scanning use. It now
/// forwards to whichever <see cref="IAiProvider"/> the server is configured with (Gemini on
/// hosted, so those calls are made exactly as before). The name is historical.
/// Never substitutes a canned reply: AI off throws AiUnavailableException, a failed call throws.
/// </summary>
public class GeminiApiClient : IGeminiApiClient
{
    private readonly IAiProvider _provider;

    public GeminiApiClient(IAiProvider provider)
    {
        _provider = provider;
    }

    public bool IsConfigured => _provider.IsConfigured;

    public async Task<string> GenerateContentAsync(string prompt, string model)
    {
        var result = await _provider.GenerateTextAsync(new AiTextRequest(prompt, model, "insights"));
        return RequireText(result);
    }

    public async Task<string> GenerateContentAsync(string prompt, string model, IReadOnlyList<(string MimeType, byte[] Data)> files)
    {
        if (files.Count == 0)
        {
            return await GenerateContentAsync(prompt, model);
        }

        var result = await _provider.GenerateTextAsync(new AiTextRequest(prompt, model, "receipts", files));
        return RequireText(result);
    }

    // The Gemini provider hands back an empty reply as empty text; treat it as the provider being
    // down, so it maps to the same 503 as other outages (05a).
    private static string RequireText(AiTextResult result) =>
        string.IsNullOrEmpty(result.Text)
            ? throw new AiUnavailableException(GeminiNativeProvider.ProviderDownMessage, null)
            : result.Text;
}
