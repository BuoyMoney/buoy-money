using System.Net;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class GeminiEmbeddingClientTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    public List<(Uri? Url, string Body)> Requests { get; } = new();
    public List<string?> ApiKeyHeaders { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Requests.Add((request.RequestUri, request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct)));
      ApiKeyHeaders.Add(request.Headers.TryGetValues("x-goog-api-key", out var v) ? v.Single() : null);
      return respond(request);
    }
  }

  private static IConfiguration Config(string? key) =>
    new ConfigurationBuilder()
      .AddInMemoryCollection(key == null ? new Dictionary<string, string?>() : new() { ["Gemini:ApiKey"] = key })
      .Build();

  // Only meaningful when GOOGLE_API_KEY / GEMINI_API_KEY are unset in the test process.
  private static bool EnvKeyPresent =>
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY")) ||
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));

  [Fact]
  public async Task Disabled_without_a_key_and_returns_null_without_calling_out()
  {
    if (EnvKeyPresent) return;
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
    var client = new GeminiEmbeddingClient(Config(null), new HttpClient(handler));

    client.IsEnabled.Should().BeFalse();
    (await client.EmbedAsync("hello")).Should().BeNull();
    handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Posts_the_verified_body_shape_and_returns_the_vector()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("{\"embedding\":{\"values\":[0.1,0.2,0.3]}}"),
    });
    var client = new GeminiEmbeddingClient(Config("test-key"), new HttpClient(handler));

    var vector = await client.EmbedAsync("coffee");

    client.IsEnabled.Should().BeTrue();
    vector.Should().Equal(0.1f, 0.2f, 0.3f);
    var (url, body) = handler.Requests.Single();
    url!.ToString().Should().Contain("models/gemini-embedding-2:embedContent");
    body.Should().Contain("\"outputDimensionality\":768").And.Contain("coffee");
  }

  [Fact]
  public async Task Sends_the_key_in_a_header_and_never_in_the_url()
  {
    // With a real key in the environment it would win over the fake one; skip rather than risk printing it.
    if (EnvKeyPresent) return;
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("{\"embedding\":{\"values\":[0.5]}}"),
    });
    var client = new GeminiEmbeddingClient(Config("fake-test-key"), new HttpClient(handler));

    await client.EmbedAsync("coffee");

    var (url, _) = handler.Requests.Single();
    url!.Query.Should().NotContain("key");
    handler.ApiKeyHeaders.Single().Should().Be("fake-test-key");
  }

  [Fact]
  public async Task Non_success_status_throws_with_status_and_body()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
    {
      Content = new StringContent("quota"),
    });
    var client = new GeminiEmbeddingClient(Config("test-key"), new HttpClient(handler));

    var act = () => client.EmbedAsync("x");

    var ex = (await act.Should().ThrowAsync<EmbeddingApiException>()).Which;
    ex.StatusCode.Should().Be(429);
    ex.ResponseBody.Should().Be("quota");
    ex.Message.Should().Be("embedContent returned 429: quota");
  }

  [Fact]
  public void Reports_model_and_dimension_in_one_place()
  {
    var client = new GeminiEmbeddingClient(Config("k"), new HttpClient());
    client.Model.Should().Be("gemini-embedding-2");
    client.Dimensions.Should().Be(768);
  }
}
