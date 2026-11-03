using BuoyAI.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// The <c>Ai:*</c> settings, read once at startup. Hosted defaults to Gemini (what it has always
/// run); self-host defaults to none, so a fresh home server makes no outside calls until the
/// owner picks a provider. An unrecognised provider name stops the app at startup instead of
/// quietly running with AI off or on the wrong service.
///
/// Settings (environment form <c>Ai__Provider</c> and so on):
/// Provider (none, gemini, ollama, openai, openrouter, gemini-compat, custom, buoy-cloud), CloudUrl and CloudCredential (buoy-cloud only), BaseUrl, ApiKey, Model,
/// Models:{feature} (per-feature override), EmbeddingModel, EmbeddingDimensions (default 768),
/// TimeoutSeconds (default 120), Temperature (default 0), ReasoningEffort (none, low, medium, high; unset sends nothing), PriceInputPerMillion and
/// PriceOutputPerMillion (USD, default 0).
/// </summary>
public sealed class AiProviderOptions
{
    public const string None = "none";
    public const string Gemini = "gemini";
    public const string Ollama = "ollama";
    public const string OpenAi = "openai";
    public const string OpenRouter = "openrouter";
    public const string GeminiCompat = "gemini-compat";
    public const string Custom = "custom";

    /// <summary>BuoyAI Cloud: AI bought from the metered gateway with this server's pairing credential.</summary>
    public const string BuoyCloud = "buoy-cloud";

    private static readonly string[] OpenAiCompatible = [Ollama, OpenAi, OpenRouter, GeminiCompat, Custom];

    /// <summary>Default base URL per preset. "custom" has none: the operator must set BaseUrl.</summary>
    public static readonly IReadOnlyDictionary<string, string> PresetBaseUrls = new Dictionary<string, string>
    {
        [Ollama] = "http://localhost:11434/v1",
        [OpenAi] = "https://api.openai.com/v1",
        [OpenRouter] = "https://openrouter.ai/api/v1",
        [GeminiCompat] = "https://generativelanguage.googleapis.com/v1beta/openai",
    };

    public string Provider { get; init; } = None;

    /// <summary>Address of the BuoyAI Cloud AI gateway (<c>Ai:CloudUrl</c>). Only used when the provider is buoy-cloud.</summary>
    public string CloudUrl { get; init; } = "";

    /// <summary>This server's pairing credential for BuoyAI Cloud (<c>Ai:CloudCredential</c>). Never shown, never logged.</summary>
    public string CloudCredential { get; init; } = "";

    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public IReadOnlyDictionary<string, string> FeatureModels { get; init; } = new Dictionary<string, string>();
    public string EmbeddingModel { get; init; } = "";
    public int EmbeddingDimensions { get; init; } = 768;
    public int TimeoutSeconds { get; init; } = 120;
    public double Temperature { get; init; }

    /// <summary>Sent as <c>reasoning_effort</c> when set. "none" turns thinking off on models that think by default (Qwen3, DeepSeek-R1), which for a one-line answer is minutes saved per call.</summary>
    public string ReasoningEffort { get; init; } = "";
    public decimal PriceInputPerMillion { get; init; }
    public decimal PriceOutputPerMillion { get; init; }

    public bool IsGemini => Provider == Gemini;
    public bool IsNone => Provider == None;
    public bool IsBuoyCloud => Provider == BuoyCloud;
    public bool IsOpenAiCompatible => OpenAiCompatible.Contains(Provider);

    /// <summary>Local models cost nothing per call; every other preset costs what its price settings say.</summary>
    public bool IsLocal => Provider == Ollama;

    /// <summary>Why an OpenAI-compatible provider can't run yet, or null when it can (or isn't one).</summary>
    public string? SetupProblem
    {
        get
        {
            if (IsBuoyCloud)
            {
                if (!Uri.TryCreate(CloudUrl, UriKind.Absolute, out var cloud) || (cloud.Scheme != "http" && cloud.Scheme != "https"))
                    return "Ai:CloudUrl must be the http or https address of BuoyAI Cloud.";
                // The pairing credential is a paid bearer secret: never send it in cleartext off this machine.
                if (cloud.Scheme == "http" && !IsLocalHost(cloud.Host))
                    return "Ai:CloudUrl must be https (plain http is only allowed for localhost).";
                if (string.IsNullOrWhiteSpace(CloudCredential))
                    return "Ai:CloudCredential is not set (pair this server with BuoyAI Cloud first).";
                return null;
            }
            if (!IsOpenAiCompatible) return null;
            if (string.IsNullOrWhiteSpace(BaseUrl))
                return "Ai:BaseUrl is not set.";
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return "Ai:BaseUrl must be an http or https address.";
            if (string.IsNullOrWhiteSpace(Model))
                return "Ai:Model is not set.";
            if (RequiresKey && string.IsNullOrWhiteSpace(ApiKey))
                return $"Ai:ApiKey is not set (the {Provider} service needs one).";
            return null;
        }
    }

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "host.docker.internal";

    private bool RequiresKey => Provider is OpenAi or OpenRouter or GeminiCompat;

    public static AiProviderOptions From(IConfiguration configuration, EditionInfo edition)
    {
        var raw = configuration["Ai:Provider"];
        var provider = string.IsNullOrWhiteSpace(raw)
            ? (edition.IsSelfHost ? None : Gemini)
            : Normalize(raw);

        if (provider is not (None or Gemini or BuoyCloud) && !OpenAiCompatible.Contains(provider))
        {
            throw new InvalidOperationException(
                $"Ai:Provider must be one of none, gemini, ollama, openai, openrouter, gemini-compat, custom, buoy-cloud (got '{raw}').");
        }

        var baseUrl = configuration["Ai:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = PresetBaseUrls.GetValueOrDefault(provider, "");

        var apiKey = configuration["Ai:ApiKey"];
        // The Gemini compatibility endpoint takes the same key as native Gemini, so reuse the
        // operator's existing Gemini setting rather than asking for it twice.
        if (string.IsNullOrWhiteSpace(apiKey) && provider == GeminiCompat)
            apiKey = GeminiApiKey.ResolveIgnoringProvider(configuration);

        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in configuration.GetSection("Ai:Models").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value)) models[child.Key] = child.Value.Trim();
        }

        return new AiProviderOptions
        {
            Provider = provider,
            CloudUrl = (configuration["Ai:CloudUrl"] ?? "").Trim().TrimEnd('/'),
            CloudCredential = (configuration["Ai:CloudCredential"] ?? "").Trim(),
            BaseUrl = (baseUrl ?? "").Trim().TrimEnd('/'),
            ApiKey = (apiKey ?? "").Trim(),
            Model = (configuration["Ai:Model"] ?? "").Trim(),
            FeatureModels = models,
            EmbeddingModel = (configuration["Ai:EmbeddingModel"] ?? "").Trim(),
            EmbeddingDimensions = Positive(configuration["Ai:EmbeddingDimensions"], 768),
            TimeoutSeconds = Positive(configuration["Ai:TimeoutSeconds"], 120),
            Temperature = double.TryParse(configuration["Ai:Temperature"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0,
            ReasoningEffort = (configuration["Ai:ReasoningEffort"] ?? "").Trim().ToLowerInvariant(),
            PriceInputPerMillion = Decimal(configuration["Ai:PriceInputPerMillion"]),
            PriceOutputPerMillion = Decimal(configuration["Ai:PriceOutputPerMillion"]),
        };
    }

    /// <summary>Lower-cases the name and maps the old example-file value "GoogleGemini" (and anything starting "google") to gemini.</summary>
    internal static string Normalize(string raw)
    {
        var name = raw.Trim().ToLowerInvariant();
        return name.StartsWith("google", StringComparison.Ordinal) ? Gemini : name;
    }

    private static int Positive(string? value, int fallback) =>
        int.TryParse(value, out var n) && n > 0 ? n : fallback;

    private static decimal Decimal(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : 0;
}
