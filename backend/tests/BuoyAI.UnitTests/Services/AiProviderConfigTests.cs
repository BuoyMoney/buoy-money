using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>Which provider a server runs, and what that does to everything that used to assume Gemini.</summary>
public class AiProviderConfigTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value)).Build();

    private static AiProviderOptions Options(params (string Key, string Value)[] settings)
    {
        var config = Config(settings);
        return AiProviderOptions.From(config, EditionInfo.From(config));
    }

    // ---- Defaults and validation -----------------------------------------------------------

    [Theory]
    [InlineData("GoogleGemini")]
    [InlineData("google")]
    public void OldExampleFileValue_GoogleGemini_MeansGemini_InsteadOfStoppingStartup(string value)
    {
        var config = Config(("AI:Provider", value));
        AiProviderOptions.From(config, EditionInfo.From(config)).Provider.Should().Be("gemini");
        GeminiApiKey.IsGeminiProvider(config).Should().BeTrue();
    }

    [Theory]
    [InlineData("appsettings.json.example")]
    [InlineData("appsettings.Development.json.example")]
    public void TrackedExampleSettingsFiles_StillStartTheAiProvider(string file)
    {
        var dir = AppContext.BaseDirectory;
        string? path = null;
        while (dir != null && path == null)
        {
            var candidate = Path.Combine(dir, "backend", "src", "BuoyAI.API", file);
            if (File.Exists(candidate)) path = candidate;
            dir = Path.GetDirectoryName(dir);
        }
        path.Should().NotBeNull();
        var config = new ConfigurationBuilder().AddJsonFile(path!).Build();
        var act = () => AiProviderOptions.From(config, EditionInfo.From(config));
        act.Should().NotThrow();
    }

    [Fact]
    public void Hosted_DefaultsToGemini_SoNothingChanges()
    {
        var options = Options();
        options.Provider.Should().Be("gemini");
        options.IsGemini.Should().BeTrue();
        options.SetupProblem.Should().BeNull();
    }

    [Fact]
    public void SelfHost_DefaultsToNone_AiIsOffUntilTheOwnerChoosesAProvider()
    {
        var options = Options(("BuoyMoney:Edition", "selfhost"));
        options.Provider.Should().Be("none");
        options.IsNone.Should().BeTrue();
    }

    [Theory]
    [InlineData("ollama", "http://localhost:11434/v1")]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("openrouter", "https://openrouter.ai/api/v1")]
    [InlineData("gemini-compat", "https://generativelanguage.googleapis.com/v1beta/openai")]
    public void Presets_FillInTheBaseUrl(string preset, string url)
    {
        Options(("Ai:Provider", preset)).BaseUrl.Should().Be(url);
    }

    [Fact]
    public void BaseUrl_CanBeOverridden_AndLosesItsTrailingSlash()
    {
        Options(("Ai:Provider", "ollama"), ("Ai:BaseUrl", "http://host.docker.internal:11434/v1/")).BaseUrl
            .Should().Be("http://host.docker.internal:11434/v1");
    }

    [Fact]
    public void UnknownProvider_StopsStartup_InsteadOfGuessing()
    {
        var act = () => Options(("Ai:Provider", "chatgpt"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Ai:Provider*chatgpt*");
    }

    [Theory]
    [InlineData("custom", "Ai:BaseUrl")]
    [InlineData("ollama", "Ai:Model")]
    public void SetupProblem_NamesTheMissingSetting(string provider, string missing)
    {
        Options(("Ai:Provider", provider)).SetupProblem.Should().Contain(missing);
    }

    [Fact]
    public void CloudPresets_NeedAKey_ButOllamaDoesNot()
    {
        Options(("Ai:Provider", "openai"), ("Ai:Model", "gpt-x")).SetupProblem.Should().Contain("Ai:ApiKey");
        Options(("Ai:Provider", "ollama"), ("Ai:Model", "qwen2.5:7b")).SetupProblem.Should().BeNull();
    }

    [Fact]
    public void GeminiCompat_ReusesTheGeminiKeyTheOwnerAlreadySet()
    {
        var options = Options(("Ai:Provider", "gemini-compat"), ("Ai:Model", "gemini-2.5-flash"), ("Gemini:ApiKey", "abc"));
        options.ApiKey.Should().Be("abc");
    }

    [Fact]
    public void BadBaseUrl_IsReported()
    {
        Options(("Ai:Provider", "custom"), ("Ai:BaseUrl", "ftp://x"), ("Ai:Model", "m")).SetupProblem.Should().Contain("http");
    }

    [Fact]
    public void NativeGemini_WithoutAKey_NamesTheMissingKey()
    {
        var options = Options(("Ai:Provider", "gemini"));
        AiFeatureCatalog.SetupProblemFor(options).Should().Contain("GOOGLE_API_KEY");
        AiFeatureCatalog.SetupProblemFor(Options(("Ai:Provider", "none"))).Should().BeNull();
    }

    // ---- The Gemini key follows the provider ------------------------------------------------

    [Fact]
    public void GeminiKey_OnHostedDefault_IsResolvedAsAlways()
    {
        GeminiApiKey.Resolve(Config(("Gemini:ApiKey", "k")), _ => null).Should().Be("k");
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("none")]
    [InlineData("openai")]
    public void GeminiKey_WithAnotherProvider_IsOff_EvenIfAKeyIsInTheEnvironment(string provider)
    {
        var config = Config(("Ai:Provider", provider), ("Gemini:ApiKey", "k"));
        GeminiApiKey.Resolve(config, _ => "env-key").Should().BeEmpty();
        GeminiApiKey.ResolveConfigOnly(config).Should().BeEmpty();
        GeminiApiKey.ResolveConfigFirstNullCoalesced(config, _ => "env-key").Should().BeEmpty();
    }

    [Fact]
    public void GeminiKey_OnSelfHostWithNothingChosen_IsOff()
    {
        GeminiApiKey.Resolve(Config(("BuoyMoney:Edition", "selfhost"), ("Gemini:ApiKey", "k")), _ => "env").Should().BeEmpty();
    }

    [Fact]
    public void GeminiKey_OnSelfHostThatChoseGemini_Works()
    {
        GeminiApiKey.Resolve(Config(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "gemini"), ("Gemini:ApiKey", "k")), _ => null)
            .Should().Be("k");
    }

    // ---- The container picks the right classes ---------------------------------------------

    private static ServiceProvider Build(params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "your-super-secret-jwt-key-that-should-be-at-least-32-characters-long",
            ["Jwt:Issuer"] = "BuoyAI",
            ["Jwt:Audience"] = "BuoyAI-Users",
            ["Encryption:Key"] = "test-encryption-key-for-unit-tests",
        };
        foreach (var (k, v) in extra) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Development");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(env.Object);
        services.AddSingleton<Qdrant.Client.QdrantClient>(_ => null!);
        services.AddInfrastructure(config, skipDbContext: true);
        services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()), ServiceLifetime.Scoped);
        return services.BuildServiceProvider();
    }

    // Every configured provider is wrapped so the calls a user causes write usage rows.
    private static IAiProvider Inner(IAiProvider provider) =>
        provider is MeteredAiProvider metered ? metered.Inner : provider;

    [Fact]
    public void Container_Hosted_RunsGemini_ForTextAndEmbeddings()
    {
        using var sp = Build();
        Inner(sp.GetRequiredService<IAiProvider>()).Should().BeOfType<GeminiNativeProvider>();
        sp.GetRequiredService<IEmbeddingClient>().Should().BeOfType<BuoyAI.Infrastructure.Services.GeminiEmbeddingClient>();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IGeminiApiClient>().Should().BeOfType<BuoyAI.Infrastructure.Services.GeminiApiClient>();
    }

    [Fact]
    public void Container_SelfHostDefault_AiIsOffEverywhere_AndANullProviderIsRegistered()
    {
        using var sp = Build(("BuoyMoney:Edition", "selfhost"), ("Gemini:ApiKey", "stray-key"));
        var ai = sp.GetRequiredService<IAiProvider>();
        ai.Should().BeOfType<NullAiProvider>();
        ai.IsConfigured.Should().BeFalse();
        sp.GetRequiredService<IEmbeddingClient>().IsEnabled.Should().BeFalse();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IGeminiApiClient>().IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void Container_Ollama_UsesTheCompatibleProvider_AndItsEmbeddingModelWhenGiven()
    {
        using var sp = Build(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "ollama"), ("Ai:Model", "qwen2.5:7b"),
            ("Ai:EmbeddingModel", "nomic-embed-text"));
        var ai = sp.GetRequiredService<IAiProvider>();
        Inner(ai).Should().BeOfType<OpenAiCompatibleProvider>();
        ai.IsConfigured.Should().BeTrue();
        var embeddings = sp.GetRequiredService<IEmbeddingClient>();
        embeddings.IsEnabled.Should().BeTrue();
        embeddings.Model.Should().Be("nomic-embed-text");
    }

    // ---- The capability matrix says what the code does -------------------------------------

    private static Dictionary<string, AiFeatureStatus> Matrix(params (string Key, string Value)[] settings)
    {
        using var sp = Build(settings);
        var provider = sp.GetRequiredService<IAiProvider>();
        var options = sp.GetRequiredService<AiProviderOptions>();
        return AiFeatureCatalog.Describe(provider, options).ToDictionary(f => f.Id);
    }

    [Fact]
    public void Matrix_AiOff_EverythingIsOff_WithTheReason()
    {
        var matrix = Matrix(("BuoyMoney:Edition", "selfhost"));
        matrix.Values.Should().OnlyContain(f => f.Status == "off");
        matrix["categorization"].Note.Should().Contain("off");
    }

    [Fact]
    public void Matrix_Ollama_TextFeaturesWork_ChatAgentsAndAttachmentsAreOff()
    {
        var matrix = Matrix(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "ollama"), ("Ai:Model", "qwen2.5:7b"));

        matrix["categorization"].Status.Should().Be("works");
        matrix["categorization"].Note.Should().Contain("7B");
        matrix["insights"].Status.Should().Be("limited");
        matrix["insights"].Note.Should().Contain("OLLAMA_CONTEXT_LENGTH");
        matrix["chat"].Status.Should().Be("off");
        matrix["drilldown"].Status.Should().Be("off");
        matrix["actions"].Status.Should().Be("off");
        matrix["attachments"].Status.Should().Be("off");
        matrix["similarity"].Status.Should().Be("off");
        matrix["similarity"].Note.Should().Contain("Ai:EmbeddingModel");
    }

    [Fact]
    public void Matrix_OllamaWithAnEmbeddingModel_TurnsSimilaritySearchOn()
    {
        var matrix = Matrix(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "ollama"), ("Ai:Model", "m"), ("Ai:EmbeddingModel", "nomic-embed-text"));
        matrix["similarity"].Status.Should().Be("works");
    }

    [Fact]
    public void Matrix_ACloudCompatibleProvider_HasNoLocalModelCaveat()
    {
        var matrix = Matrix(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "openai"), ("Ai:Model", "m"), ("Ai:ApiKey", "k"));
        matrix["insights"].Status.Should().Be("works");
        matrix["chat"].Status.Should().Be("off");
    }

    [Fact]
    public void Matrix_ProviderWithMissingSetting_IsOff_AndSaysWhichSetting()
    {
        var matrix = Matrix(("BuoyMoney:Edition", "selfhost"), ("Ai:Provider", "openai"), ("Ai:Model", "m"));
        matrix["categorization"].Status.Should().Be("off");
        matrix["categorization"].Note.Should().Contain("Ai:ApiKey");
    }

    // ---- The probe ---------------------------------------------------------------------------

    [Fact]
    public async Task Probe_AiOff_FailsInOneStepWithoutCallingAnything()
    {
        var steps = await AiProbe.RunAsync(new NullAiProvider(), "m");
        steps.Should().ContainSingle().Which.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Probe_ModelThatAnswersInTheFormat_PassesBothSteps()
    {
        var ai = new FakeAiProvider
        {
            Reply = r => new AiTextResult(r.Prompt.Contains("categorizer") ? "22:0.92:gas,fuel" : "ready", 100, 5),
        };

        var steps = await AiProbe.RunAsync(ai, "m");

        steps.Select(s => s.Id).Should().Equal("reach", "categorization");
        steps.Should().OnlyContain(s => s.Ok);
        steps[1].InputTokens.Should().Be(100);
        ai.Requests[1].Prompt.Should().Contain("SHELL OIL"); // the real prompt, not a stand-in
    }

    [Fact]
    public async Task Probe_ModelThatChatters_FailsTheFormatStep_WithAHint()
    {
        var ai = new FakeAiProvider { Reply = _ => new AiTextResult("Sure! This looks like a coffee purchase.", 1, 1) };

        var steps = await AiProbe.RunAsync(ai, "m");

        steps[0].Ok.Should().BeTrue();
        steps[1].Ok.Should().BeFalse();
        steps[1].Detail.Should().Contain("CATEGORY_ID:CONFIDENCE");
    }

    [Fact]
    public async Task Probe_ServerDown_StopsAtTheFirstStep_WithTheProvidersMessage()
    {
        var ai = new FakeAiProvider { Reply = _ => throw new BuoyAI.Core.Exceptions.AiUnavailableException("Could not reach the AI service", null) };

        var steps = await AiProbe.RunAsync(ai, "m");

        steps.Should().ContainSingle().Which.Detail.Should().Contain("Could not reach");
    }
}
