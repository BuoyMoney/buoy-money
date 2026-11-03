using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>A provider whose replies and configured state the test controls; records every request.</summary>
internal sealed class FakeAiProvider : IAiProvider
{
    public string Id { get; init; } = "gemini";
    public bool IsConfigured { get; set; } = true;
    public AiCapabilities Capabilities { get; init; } = new(true, true, true, true);
    public string? ModelOverride { get; init; }
    public Func<AiTextRequest, AiTextResult> Reply { get; set; } = _ => new AiTextResult("", null, null);
    public List<AiTextRequest> Requests { get; } = new();

    public string ResolveModel(string requestedModel, string feature) => ModelOverride ?? requestedModel;

    public Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new AiUnavailableException("off", null);
        Requests.Add(request);
        return Task.FromResult(Reply(request));
    }
}

/// <summary>
/// Pins what the migrated Gemini call sites send to the provider and what they do with the
/// answer. Hosted runs on the Gemini provider, so these are the hosted behaviours: same model,
/// same prompt, same usage row, same fallbacks. Written against the seam so the next provider
/// can't change them.
/// </summary>
public class AiProviderSeamTests
{
    private readonly Mock<IAIUsageTracker> _usage = new();
    private readonly Mock<ICostTrackingService> _cost = new();
    private readonly Mock<ITransactionRepository> _transactions = new();
    private readonly Mock<IEntitlementService> _entitlements = new();

    private AICategorizationService CreateCategorizer(FakeAiProvider ai)
    {
        var config = new ConfigurationBuilder().Build();
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _entitlements.Setup(e => e.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Standard);
        return new AICategorizationService(
            _transactions.Object, new Mock<IUserRepository>().Object, _entitlements.Object,
            new Mock<ICategorizationRuleService>().Object, new Mock<ITransactionEmbeddingService>().Object,
            new Mock<ICategoryRepository>().Object, new BuoyAIDbContext(options), config,
            NullLogger<AICategorizationService>.Instance, new AIModelConfiguration(config),
            TestGeminiUsage.Create(), _usage.Object, _cost.Object, ai);
    }

    private static Transaction Coffee() => new()
    {
        Id = 7, Amount = 5.75m, Type = TransactionType.Debit, MerchantName = "Blue Bottle Coffee",
        Description = "SQ *BLUE BOTTLE", Date = DateTime.UtcNow.Date,
    };

    [Fact]
    public async Task Categorization_SendsTierModelAndFeature_RecordsProviderTokensAtTheGeminiTablePrice()
    {
        var ai = new FakeAiProvider { Reply = _ => new AiTextResult("  21:0.95:coffee,cafe\n", 1800, 12) };
        var tierModel = new AIModelConfiguration(new ConfigurationBuilder().Build()).GetModelsForTier(SubscriptionTier.Standard)[0];
        _cost.Setup(c => c.CalculateCost(tierModel, 1800, 12)).Returns(0.00064m);
        _transactions.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(Coffee());

        var result = await CreateCategorizer(ai).CategorizeTransactionAsync(7, userId: 3);

        result.Error.Should().BeNull();
        result.SuggestedCategoryId.Should().Be(21);
        result.Confidence.Should().Be(0.95);
        ai.Requests.Should().ContainSingle();
        ai.Requests[0].Model.Should().Be(tierModel);
        ai.Requests[0].Feature.Should().Be("categorization");
        ai.Requests[0].Files.Should().BeNull();
        // The provider's own split (1800 in, 12 out), not one total divided 70/30; real counts, so not flagged as estimated.
        _usage.Verify(u => u.RecordCallAsync(It.Is<AiUsageEntry>(e =>
            e.UserId == 3 && e.Model == tierModel && e.Provider == "gemini" && e.InputTokens == 1800 && e.OutputTokens == 12
            && !e.TokensEstimated && e.Cost == 0.00064m && e.RequestContext == AICategorizationService.UsageContext
            && e.CountsAgainstRequestLimits)), Times.Once);
    }

    [Fact]
    public async Task Categorization_ProviderThatPricesItself_WinsOverTheGeminiTable_AndShowsItsOwnModel()
    {
        var ai = new FakeAiProvider
        {
            Id = "ollama",
            ModelOverride = "qwen2.5:7b",
            Reply = _ => new AiTextResult("21:0.9", 500, 6, CostUsd: 0m),
        };
        _transactions.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(Coffee());

        await CreateCategorizer(ai).CategorizeTransactionAsync(7, 3);

        _usage.Verify(u => u.RecordCallAsync(It.Is<AiUsageEntry>(e =>
            e.UserId == 3 && e.Model == "qwen2.5:7b" && e.Provider == "ollama" && e.InputTokens == 500 && e.OutputTokens == 6
            && e.Cost == 0m && e.RequestContext == "categorization")), Times.Once);
        _cost.Verify(c => c.CalculateCost(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Categorization_LongModelName_IsCutToTheColumnWidth()
    {
        var longName = "openrouter/some-vendor/some-very-long-model-name-instruct:free-extended";
        var ai = new FakeAiProvider { Id = "openrouter", ModelOverride = longName, Reply = _ => new AiTextResult("21:0.9", 5, 5, 0m) };
        _transactions.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(Coffee());

        await CreateCategorizer(ai).CategorizeTransactionAsync(7, 3);

        var cut = longName.Substring(0, 50);
        _usage.Verify(u => u.RecordCallAsync(It.Is<AiUsageEntry>(e =>
            e.UserId == 3 && e.Model == cut && e.InputTokens == 5 && e.OutputTokens == 5 && e.Cost == 0m)), Times.Once);
    }

    [Fact]
    public async Task Categorization_AiOff_NeverCallsTheProvider_AndFallsBackToTheKeywordGuess()
    {
        var ai = new FakeAiProvider { IsConfigured = false };
        _transactions.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(Coffee());

        var result = await CreateCategorizer(ai).CategorizeTransactionAsync(7, 3);

        ai.Requests.Should().BeEmpty();
        result.Confidence.Should().Be(0.3);
    }

    [Fact]
    public async Task Categorization_UnreadableReply_ReturnsUncategorizedAtZeroConfidenceLikeBefore()
    {
        var ai = new FakeAiProvider { Reply = _ => new AiTextResult("I think it is a coffee shop.", 10, 10) };
        _transactions.Setup(t => t.GetByIdAsync(7)).ReturnsAsync(Coffee());

        var result = await CreateCategorizer(ai).CategorizeTransactionAsync(7, 3);

        // The parser gives Uncategorized at 0; the service then swaps in the keyword guess.
        result.Error.Should().BeNull();
        result.Confidence.Should().BeLessThanOrEqualTo(0.3);
    }

    [Fact]
    public void CategorizationParser_AcceptsTheFormatTheBenchmarkAndProbeRely_OnItsOwn()
    {
        var parsed = AICategorizationService.ParseCategorizationResponse("21:0.9:coffee,cafe\nextra line", new List<Category>());

        parsed.SystemCategory.Should().Be((TransactionCategory)21);
        parsed.Confidence.Should().Be(0.9);
    }

    // ---- Facade ---------------------------------------------------------------------------

    [Fact]
    public async Task GeminiApiClient_ForwardsTextToTheProvider_AsTheInsightsFeature_Untouched()
    {
        var ai = new FakeAiProvider { Reply = _ => new AiTextResult("  raw reply \n", 1, 1) };

        var text = await new GeminiApiClient(ai).GenerateContentAsync("the prompt", "gemini-x");

        text.Should().Be("  raw reply \n");
        ai.Requests.Should().ContainSingle().Which.Should().Be(new AiTextRequest("the prompt", "gemini-x", "insights"));
    }

    [Fact]
    public async Task GeminiApiClient_WithFiles_ForwardsThemAsTheReceiptsFeature()
    {
        var ai = new FakeAiProvider { Reply = _ => new AiTextResult("ok", 1, 1) };
        var files = new List<(string, byte[])> { ("image/png", new byte[] { 1, 2 }) };

        await new GeminiApiClient(ai).GenerateContentAsync("p", "m", files);

        ai.Requests[0].Feature.Should().Be("receipts");
        ai.Requests[0].Files.Should().HaveCount(1);
    }

    [Fact]
    public async Task GeminiApiClient_WithNullProvider_ReportsNotConfigured_AndThrowsInsteadOfInventingText()
    {
        var client = new GeminiApiClient(new NullAiProvider());

        client.IsConfigured.Should().BeFalse();
        var act = () => client.GenerateContentAsync("anything", "m");
        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Message.Should().Contain("turned off");
    }

    // ---- Memory extraction keeps the hosted gate -------------------------------------------

    private static (AiMemoryService Service, BuoyAIDbContext Db) MemoryService(IAiProvider ai, string? geminiConfigKey)
    {
        var settings = new Dictionary<string, string?>();
        if (geminiConfigKey != null) settings["Gemini:ApiKey"] = geminiConfigKey;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new BuoyAIDbContext(options);
        return (new AiMemoryService(db, config, NullLogger<AiMemoryService>.Instance, TestGeminiUsage.Create(), ai), db);
    }

    private const string Message = "My wife's birthday is on March 15 and I am saving for a house.";
    private const string MemoryJson =
        "[{\"memoryType\":\"date\",\"memoryKey\":\"wife_birthday\",\"memoryValue\":\"March 15\",\"displayText\":\"Wife's birthday is March 15\",\"confidence\":0.9}]";

    [Fact]
    public async Task Memory_OnGemini_StaysOffWithoutTheConfigKey_EvenWhenTheProviderHasAKey()
    {
        // Hosted shape: the Gemini provider works (GOOGLE_API_KEY) but Gemini:ApiKey is blank.
        var ai = new FakeAiProvider { Id = "gemini", Reply = _ => new AiTextResult(MemoryJson, 1, 1) };
        var (service, db) = MemoryService(ai, geminiConfigKey: "");

        await service.ExtractAndStoreMemoriesAsync(1, Guid.NewGuid(), Message, "user");

        ai.Requests.Should().BeEmpty();
        db.UserAiMemories.Should().BeEmpty();
    }

    [Fact]
    public async Task Memory_OnGemini_WithTheConfigKey_ExtractsWithTheSameModelAsBefore()
    {
        var ai = new FakeAiProvider { Id = "gemini", Reply = _ => new AiTextResult("```json\n" + MemoryJson + "\n```", 1, 1) };
        var (service, db) = MemoryService(ai, geminiConfigKey: "key");

        await service.ExtractAndStoreMemoriesAsync(1, Guid.NewGuid(), Message, "user");

        ai.Requests.Should().ContainSingle().Which.Model.Should().Be("gemini-2.5-flash");
        ai.Requests[0].Feature.Should().Be("memory-extraction");
        db.UserAiMemories.Should().ContainSingle(m => m.MemoryKey == "wife_birthday");
    }

    [Fact]
    public async Task Memory_OnAnotherProvider_RunsWhenThatProviderIsSetUp()
    {
        var ai = new FakeAiProvider { Id = "ollama", Reply = _ => new AiTextResult(MemoryJson, 1, 1) };
        var (service, db) = MemoryService(ai, geminiConfigKey: null);

        await service.ExtractAndStoreMemoriesAsync(1, Guid.NewGuid(), Message, "user");

        db.UserAiMemories.Should().ContainSingle();
    }

    // ---- Native provider --------------------------------------------------------------------

    [Fact]
    public async Task GeminiNativeProvider_WithoutKey_IsNotConfigured_AndThrowsTheOldMessage()
    {
        if (GeminiApiKey.IsConfigured(new ConfigurationBuilder().Build())) return; // a real key is in the environment

        var provider = new GeminiNativeProvider(new ConfigurationBuilder().Build(), NullLogger<GeminiNativeProvider>.Instance, null);

        provider.IsConfigured.Should().BeFalse();
        provider.Id.Should().Be("gemini");
        provider.ResolveModel("gemini-3.5-flash-lite", "categorization").Should().Be("gemini-3.5-flash-lite");
        var act = () => provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));
        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Message.Should().Contain("API key");
    }
}
