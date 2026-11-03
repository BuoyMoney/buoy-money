using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>One row of the settings screen's capability matrix. Status is "works", "limited" or "off".</summary>
public sealed record AiFeatureStatus(string Id, string Name, string Status, string Note);

/// <summary>
/// What each AI feature does with the provider this server is configured with. The rows follow
/// what the code does today, not what the model could do in theory: chat, agents and attachments
/// still run only on Gemini, so with any other provider they are "off" and the app hides them.
/// The notes about model size are guidance from the research and the benchmark, not guarantees;
/// the probe on the settings screen is the live check.
/// </summary>
public static class AiFeatureCatalog
{
    public const string Works = "works";
    public const string Limited = "limited";
    public const string Off = "off";

    /// <summary>Why the provider isn't running. Native Gemini has no URL or model to check; its only setup is the key.</summary>
    public static string? SetupProblemFor(AiProviderOptions options) =>
        options.SetupProblem
        ?? (options.IsGemini ? "No Gemini API key is set (GOOGLE_API_KEY or Gemini:ApiKey)." : null);

    public static IReadOnlyList<AiFeatureStatus> Describe(IAiProvider provider, AiProviderOptions options)
    {
        var rows = new List<AiFeatureStatus>();
        var on = provider.IsConfigured;
        var gemini = on && provider.Id == AiProviderOptions.Gemini;
        var local = options.IsLocal;
        var caps = provider.Capabilities;
        var offWhy = options.IsNone
            ? "AI is off on this server."
            : provider.IsConfigured ? "" : $"Not set up: {SetupProblemFor(options)}";

        void Add(string id, string name, bool available, string status, string note) =>
            rows.Add(new AiFeatureStatus(id, name, !on ? Off : available ? status : Off, !on ? offWhy : note));

        Add("categorization", "Transaction categorization", caps.Text, Works,
            local
                ? "Works on most models of 7B and up. Smaller models send more rows to the review list."
                : "Works.");
        Add("subscriptions", "Subscription insights", caps.Text, Works,
            local ? "Works on most models of 7B and up; the detection itself needs no AI." : "Works. Detection itself needs no AI.");
        Add("wrapped", "Monthly recap wording", caps.Text, Works,
            "Works, and uses the insights model. If the model's wording is unusable, plain wording is used instead.");
        Add("insights", "Dashboard insights", caps.Text, local ? Limited : Works,
            local
                ? "Needs a model that follows JSON well: 14B or larger, with a context window of at least 16k. Ollama defaults to 4k unless you raise OLLAMA_CONTEXT_LENGTH, and the end of long prompts is silently cut off."
                : "Works.");
        Add("similarity", "Similar-transaction and help search", caps.Embeddings, Works,
            gemini
                ? "Works."
                : "Needs an embedding model. Set Ai:EmbeddingModel (for Ollama: nomic-embed-text or embeddinggemma) and keep Ai:EmbeddingDimensions matching it.");
        Add("chat", "Chat that looks things up in your data", caps.ChatAndAgents, Works,
            "Runs on Gemini only for now. With another provider the chat is hidden.");
        Add("drilldown", "Insight drill-down", caps.ChatAndAgents, Works,
            "Needs tool calling, which runs on Gemini only for now.");
        Add("actions", "Let AI change your budget", caps.ChatAndAgents, Works,
            "Needs tool calling, which runs on Gemini only for now. It always asks you first.");
        Add("attachments", "Receipt and photo scanning", caps.Attachments, Works,
            "Photos and PDFs go to Gemini only for now.");
        return rows;
    }
}
