using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// LIVE probe: mirrors ChatEmbeddingService.GenerateEmbeddingAsync's exact REST call + record shapes,
/// to prove the C# serialization/parsing round-trips against the real embedContent API. Skipped
/// unless GEMINI_LIVE_KEY is set. (The Mscc SDK path was rejected with INVALID_ARGUMENT in prod.)
/// </summary>
public class EmbeddingProbeTests
{
    private readonly ITestOutputHelper _out;
    public EmbeddingProbeTests(ITestOutputHelper output) => _out = output;

    private const string EmbeddingModelName = "gemini-embedding-2";
    private const int EmbeddingDimensions = 768;
    private static readonly HttpClient _httpClient = new();

    private sealed record EmbedContentResponse(EmbeddingValues? Embedding);
    private sealed record EmbeddingValues(float[]? Values);

    [Fact]
    public async Task LiveEmbeddingProbe()
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_LIVE_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) { _out.WriteLine("GEMINI_LIVE_KEY not set — skipping."); return; }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{EmbeddingModelName}:embedContent?key={apiKey}";
        var payload = new
        {
            model = $"models/{EmbeddingModelName}",
            content = new { parts = new[] { new { text = "my biggest grocery budget category this month" } } },
            outputDimensionality = EmbeddingDimensions,
        };

        using var response = await _httpClient.PostAsJsonAsync(url, payload);
        var body = await response.Content.ReadAsStringAsync();
        _out.WriteLine($"HTTP {(int)response.StatusCode}");
        Assert.True(response.IsSuccessStatusCode, $"embedContent failed: {body}");

        var parsed = System.Text.Json.JsonSerializer.Deserialize<EmbedContentResponse>(body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var values = parsed?.Embedding?.Values;
        _out.WriteLine($"vector length: {values?.Length}");
        Assert.NotNull(values);
        Assert.Equal(EmbeddingDimensions, values!.Length);
    }
}
