using System.Net;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class OpenAiCompatibleProviderTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public Func<HttpResponseMessage> Respond = () => Json(HttpStatusCode.OK, "{}");
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Throw != null) throw Throw;
            return Respond();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal static AiProviderOptions Options(Dictionary<string, string?> settings, bool selfHost = true)
    {
        var all = new Dictionary<string, string?>(settings);
        if (selfHost) all["BuoyMoney:Edition"] = "selfhost";
        var config = new ConfigurationBuilder().AddInMemoryCollection(all).Build();
        return AiProviderOptions.From(config, EditionInfo.From(config));
    }

    private static (OpenAiCompatibleProvider Provider, StubHandler Handler) Make(Dictionary<string, string?> settings)
    {
        var handler = new StubHandler();
        var provider = new OpenAiCompatibleProvider(Options(settings), new HttpClient(handler), NullLogger<OpenAiCompatibleProvider>.Instance);
        return (provider, handler);
    }

    private static Dictionary<string, string?> Ollama() => new()
    {
        ["Ai:Provider"] = "ollama",
        ["Ai:Model"] = "qwen2.5:7b",
    };

    private static Dictionary<string, string?> With(Dictionary<string, string?> baseSettings, string key, string value)
    {
        var copy = new Dictionary<string, string?>(baseSettings) { [key] = value };
        return copy;
    }

    private const string GoodReply =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"21:0.9:coffee,cafe\"}}],\"usage\":{\"prompt_tokens\":420,\"completion_tokens\":9}}";

    [Fact]
    public async Task Ollama_PostsAChatCompletion_WithTheConfiguredModel_AndNoAuthHeader()
    {
        var (provider, handler) = Make(Ollama());
        handler.Respond = () => Json(HttpStatusCode.OK, GoodReply);

        var result = await provider.GenerateTextAsync(new AiTextRequest("categorize this", "gemini-3.5-flash-lite", "categorization"));

        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().Should().Be("http://localhost:11434/v1/chat/completions");
        handler.Request.Headers.Authorization.Should().BeNull();
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("model").GetString().Should().Be("qwen2.5:7b"); // never the Gemini name
        body.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("temperature").GetDouble().Should().Be(0);
        var message = body.RootElement.GetProperty("messages")[0];
        message.GetProperty("role").GetString().Should().Be("user");
        message.GetProperty("content").GetString().Should().Be("categorize this");

        result.Text.Should().Be("21:0.9:coffee,cafe");
        result.InputTokens.Should().Be(420);
        result.OutputTokens.Should().Be(9);
        result.CostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task OpenAi_SendsTheBearerKey_AndPricesFromTheConfiguredRates()
    {
        var (provider, handler) = Make(new()
        {
            ["Ai:Provider"] = "openai", ["Ai:Model"] = "gpt-x", ["Ai:ApiKey"] = "sk-test",
            ["Ai:PriceInputPerMillion"] = "0.5", ["Ai:PriceOutputPerMillion"] = "2",
        });
        handler.Respond = () => Json(HttpStatusCode.OK, GoodReply);

        var result = await provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        handler.Request!.RequestUri!.ToString().Should().Be("https://api.openai.com/v1/chat/completions");
        handler.Request.Headers.Authorization!.ToString().Should().Be("Bearer sk-test");
        // 420 in at $0.5 per million plus 9 out at $2 per million
        result.CostUsd.Should().Be(420m / 1_000_000m * 0.5m + 9m / 1_000_000m * 2m);
    }

    [Fact]
    public async Task OpenRouter_ItsOwnReportedCost_WinsOverTheRates()
    {
        var (provider, handler) = Make(new() { ["Ai:Provider"] = "openrouter", ["Ai:Model"] = "x/y", ["Ai:ApiKey"] = "k" });
        handler.Respond = () => Json(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"hi\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"cost\":0.00123}}");

        var result = await provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        result.CostUsd.Should().Be(0.00123m);
    }

    [Fact]
    public async Task UnpricedCloudModel_CostsZero_NotSomeOtherModelsPrice()
    {
        var (provider, handler) = Make(new() { ["Ai:Provider"] = "openai", ["Ai:Model"] = "gpt-x", ["Ai:ApiKey"] = "k" });
        handler.Respond = () => Json(HttpStatusCode.OK, GoodReply);

        var result = await provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        result.CostUsd.Should().Be(0m);
    }

    [Fact]
    public async Task PerFeatureModel_OverridesTheDefault()
    {
        var (provider, handler) = Make(With(Ollama(), "Ai:Models:categorization", "llama3.2:3b"));
        handler.Respond = () => Json(HttpStatusCode.OK, GoodReply);

        await provider.GenerateTextAsync(new AiTextRequest("p", "ignored", "categorization"));
        using var first = JsonDocument.Parse(handler.Body!);
        await provider.GenerateTextAsync(new AiTextRequest("p", "ignored", "insights"));
        using var second = JsonDocument.Parse(handler.Body!);

        first.RootElement.GetProperty("model").GetString().Should().Be("llama3.2:3b");
        second.RootElement.GetProperty("model").GetString().Should().Be("qwen2.5:7b");
    }

    [Fact]
    public async Task ThinkingBlocks_AreRemovedFromTheReply()
    {
        var (provider, handler) = Make(Ollama());
        handler.Respond = () => Json(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"<think>hmm\\nthe coffee one</think>\\n21:0.8\"}}]}");

        var result = await provider.GenerateTextAsync(new AiTextRequest("p", "m", "categorization"));

        result.Text.Should().Be("21:0.8");
        result.InputTokens.Should().BeNull();
    }

    [Theory]
    [InlineData(401, "rejected the API key")]
    [InlineData(404, "ollama pull qwen2.5:7b")]
    [InlineData(429, "too many requests")]
    [InlineData(500, "returned an error (500).")]
    public async Task HttpFailures_BecomeAnAiUnavailableErrorWithAPlainMessage(int status, string expected)
    {
        var (provider, handler) = Make(Ollama());
        handler.Respond = () => Json((HttpStatusCode)status, "{\"error\":{\"message\":\"boom\"}}");

        var act = () => provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Message.Should().Contain(expected);
    }

    [Fact]
    public async Task ServerDown_SaysSoWithoutRevealingTheAddress()
    {
        var (provider, handler) = Make(Ollama());
        handler.Throw = new HttpRequestException("connection refused");

        var act = () => provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Message
            .Should().NotContain("localhost").And.Contain("running");
    }

    [Fact]
    public async Task EmptyChoices_IsAnError_NotAnEmptyAnswer()
    {
        var (provider, handler) = Make(Ollama());
        handler.Respond = () => Json(HttpStatusCode.OK, "{\"choices\":[]}");

        var act = () => provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));

        await act.Should().ThrowAsync<AiUnavailableException>();
    }

    [Fact]
    public async Task Attachments_AreRefused_WithoutCallingTheServer()
    {
        var (provider, handler) = Make(Ollama());

        var act = () => provider.GenerateTextAsync(new AiTextRequest("p", "m", "receipts", new List<(string, byte[])> { ("image/png", new byte[] { 1 }) }));

        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Message.Should().Contain("Gemini");
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task NotSetUp_ThrowsBeforeAnyRequest_AndSaysWhatIsMissing()
    {
        var (provider, handler) = Make(new() { ["Ai:Provider"] = "ollama" }); // no model

        provider.IsConfigured.Should().BeFalse();
        var act = () => provider.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));
        (await act.Should().ThrowAsync<AiUnavailableException>()).Which.Message.Should().Contain("Ai:Model");
        handler.Request.Should().BeNull();
    }

    [Fact]
    public void Capabilities_AreTextOnly_AndEmbeddingsNeedAnEmbeddingModel()
    {
        var (without, _) = Make(Ollama());
        var (with, _) = Make(With(Ollama(), "Ai:EmbeddingModel", "nomic-embed-text"));

        without.Capabilities.Should().Be(new AiCapabilities(true, false, false, false));
        with.Capabilities.Embeddings.Should().BeTrue();
        with.Capabilities.ChatAndAgents.Should().BeFalse();
    }

    [Fact]
    public async Task ReasoningEffort_IsSentOnlyWhenSet()
    {
        var (without, handlerA) = Make(Ollama());
        handlerA.Respond = () => Json(HttpStatusCode.OK, GoodReply);
        await without.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));
        using var a = JsonDocument.Parse(handlerA.Body!);

        var (with, handlerB) = Make(With(Ollama(), "Ai:ReasoningEffort", "None"));
        handlerB.Respond = () => Json(HttpStatusCode.OK, GoodReply);
        await with.GenerateTextAsync(new AiTextRequest("p", "m", "insights"));
        using var b = JsonDocument.Parse(handlerB.Body!);

        a.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        b.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("none");
    }

    // ---- Embeddings ---------------------------------------------------------------------

    [Fact]
    public async Task EmbeddingClient_PostsToEmbeddings_AndReturnsTheVector()
    {
        var handler = new StubHandler { Respond = () => Json(HttpStatusCode.OK, "{\"data\":[{\"embedding\":[0.1,0.2,0.3]}]}") };
        var settings = With(With(Ollama(), "Ai:EmbeddingModel", "nomic-embed-text"), "Ai:EmbeddingDimensions", "3");
        var client = new OpenAiCompatibleEmbeddingClient(Options(settings), new HttpClient(handler));

        var vector = await client.EmbedAsync("coffee");

        client.IsEnabled.Should().BeTrue();
        client.Model.Should().Be("nomic-embed-text");
        client.Dimensions.Should().Be(3);
        handler.Request!.RequestUri!.ToString().Should().Be("http://localhost:11434/v1/embeddings");
        vector.Should().Equal(0.1f, 0.2f, 0.3f);
    }

    [Fact]
    public async Task EmbeddingClient_WrongLength_IsRefused_SoMixedVectorsNeverGetStored()
    {
        var handler = new StubHandler { Respond = () => Json(HttpStatusCode.OK, "{\"data\":[{\"embedding\":[0.1,0.2]}]}") };
        var client = new OpenAiCompatibleEmbeddingClient(Options(With(Ollama(), "Ai:EmbeddingModel", "m")), new HttpClient(handler));

        var act = () => client.EmbedAsync("x");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Ai:EmbeddingDimensions");
    }

    [Fact]
    public async Task EmbeddingClient_WithoutAModel_IsDisabledAndCallsNothing()
    {
        var handler = new StubHandler();
        var client = new OpenAiCompatibleEmbeddingClient(Options(Ollama()), new HttpClient(handler));

        client.IsEnabled.Should().BeFalse();
        (await client.EmbedAsync("x")).Should().BeNull();
        handler.Request.Should().BeNull();
    }
}
