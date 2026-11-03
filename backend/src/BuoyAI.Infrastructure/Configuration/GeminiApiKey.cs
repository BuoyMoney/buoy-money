using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Configuration;

/// <summary>
/// The one place that decides which Gemini API key the app uses.
///
/// Order: the GOOGLE_API_KEY environment variable, then Gemini:ApiKey from configuration, then
/// the GEMINI_API_KEY environment variable. Blank and whitespace values are skipped, not just
/// null: appsettings.Production.json ships "Gemini": { "ApiKey": "" }, and IConfiguration hands
/// back "" for a present-but-empty key, so a plain null-coalesce never fell through (that bug
/// left the transaction embedding index empty in production).
///
/// Hosted staging and production only set GOOGLE_API_KEY (see the deploy workflows), so every
/// caller that used to read the environment variable first resolves to the same key as before.
/// </summary>
public static class GeminiApiKey
{
    public const string EnvVarPrimary = "GOOGLE_API_KEY";
    public const string ConfigKey = "Gemini:ApiKey";
    public const string EnvVarSecondary = "GEMINI_API_KEY";

    /// <summary>
    /// True when Gemini is the server's AI provider: <c>Ai:Provider</c> is "gemini", or unset on a
    /// hosted server (the default, and what hosted has always run). On a self-host server, or with
    /// another provider chosen, every Gemini-only feature (chat, agents, attachments, embeddings)
    /// is off even if a stray Gemini key is in the environment, so "AI off" and "local model only"
    /// mean what they say. Read from configuration with no dependency on the container, because
    /// the key lookups run before and outside it.
    /// </summary>
    public static bool IsGeminiProvider(IConfiguration configuration)
    {
        var provider = configuration["Ai:Provider"];
        if (!string.IsNullOrWhiteSpace(provider))
            return BuoyAI.Infrastructure.Services.Ai.AiProviderOptions.Normalize(provider) == "gemini";
        return !EditionInfo.From(configuration).IsSelfHost;
    }

    /// <summary>The key to use, or an empty string when none is configured (AI is off) or Gemini isn't the provider.</summary>
    public static string Resolve(IConfiguration configuration) =>
        Resolve(configuration, Environment.GetEnvironmentVariable);

    internal static string Resolve(IConfiguration configuration, Func<string, string?> getEnv) =>
        IsGeminiProvider(configuration) ? ResolveIgnoringProvider(configuration, getEnv) : "";

    /// <summary>The Gemini key whatever the provider is. Only for the Gemini compatibility preset, which reuses it.</summary>
    internal static string ResolveIgnoringProvider(IConfiguration configuration) =>
        ResolveIgnoringProvider(configuration, Environment.GetEnvironmentVariable);

    private static string ResolveIgnoringProvider(IConfiguration configuration, Func<string, string?> getEnv) =>
        FirstNonBlank(
            getEnv(EnvVarPrimary),
            configuration[ConfigKey],
            getEnv(EnvVarSecondary)) ?? "";

    /// <summary>True when <see cref="Resolve(IConfiguration)"/> would return a usable key.</summary>
    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(Resolve(configuration));

    /// <summary>
    /// Key lookup for AiMemoryService, kept exactly as it was before the resolver existed:
    /// Gemini:ApiKey only, no environment variable. Hosted sets only GOOGLE_API_KEY and ships
    /// Gemini:ApiKey as "", so memory extraction has never run there. Using
    /// <see cref="Resolve(IConfiguration)"/> would switch it on (a Gemini call per chat message
    /// and new stored memories). Remove this method to opt in deliberately.
    /// </summary>
    public static string ResolveConfigOnly(IConfiguration configuration) =>
        IsGeminiProvider(configuration) ? configuration[ConfigKey] ?? "" : "";

    /// <summary>
    /// Key lookup for the Gemini prompt cache, kept exactly as it was before the resolver
    /// existed: Gemini:ApiKey first with a plain null-coalesce, so the "" shipped in
    /// appsettings.Production.json wins and the cache stays off on hosted. Using
    /// <see cref="Resolve(IConfiguration)"/> would switch it on. Remove this method to opt in.
    /// </summary>
    public static string ResolveConfigFirstNullCoalesced(IConfiguration configuration) =>
        ResolveConfigFirstNullCoalesced(configuration, Environment.GetEnvironmentVariable);

    internal static string ResolveConfigFirstNullCoalesced(IConfiguration configuration, Func<string, string?> getEnv) =>
        !IsGeminiProvider(configuration)
            ? ""
            : configuration[ConfigKey]
                ?? getEnv(EnvVarPrimary)
                ?? getEnv(EnvVarSecondary)
                ?? "";

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
