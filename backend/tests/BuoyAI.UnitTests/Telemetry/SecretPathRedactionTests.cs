using System.Net;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Services.CloudPortal;
using BuoyAI.UnitTests.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BuoyAI.UnitTests.Telemetry;

/// <summary>Cloud link codes ride in the URL path; they must not reach logs, breadcrumbs, traces or error reports.</summary>
public class SecretPathRedactionTests
{
  [Theory]
  [InlineData("v1/pairing/link-sessions/BCDF-GH23", "v1/pairing/link-sessions/[code]")]
  [InlineData("v1/pairing/link-sessions/BCDF-GH23/approve", "v1/pairing/link-sessions/[code]/approve")]
  [InlineData("/api/cloud-servers/link-sessions/BCDF-GH23/deny", "/api/cloud-servers/link-sessions/[code]/deny")]
  [InlineData("https://buoy-ai.com/api/cloud-servers/link-sessions/bcdf-gh23?x=1", "https://buoy-ai.com/api/cloud-servers/link-sessions/[code]?x=1")]
  [InlineData("v1/pairing/link-sessions/poll", "v1/pairing/link-sessions/poll")]
  [InlineData("v1/pairing/servers", "v1/pairing/servers")]
  public void Redact_ReplacesOnlyTheLinkCode(string input, string expected) =>
    SecretPathRedaction.Redact(input).Should().Be(expected);

  [Fact]
  public void Redact_Null_StaysNull() => SecretPathRedaction.Redact(null).Should().BeNull();

  [Theory]
  [InlineData("https://cloud.test/v1/pairing/link-sessions/BCDF-GH23", true)]
  [InlineData("https://bridge.simplefin.org/simplefin/claim/abc", true)]
  [InlineData("https://cloud.test/v1/pairing/link-sessions/poll", false)]
  [InlineData("https://cloud.test/v1/pairing/servers", false)]
  [InlineData(null, false)]
  public void CarriesSecret_SpotsLinkCodesAndSimpleFinClaims(string? url, bool expected) =>
    SecretPathRedaction.CarriesSecret(url).Should().Be(expected);

  [Theory]
  [InlineData("/api/cloud-servers/link-sessions/BCDF-GH23", true)]
  [InlineData("/v1/pairing/link-sessions/BCDF-GH23/approve", true)]
  [InlineData("/simplefin/claim/xyz", true)]
  [InlineData("/v1/pairing/link-sessions/poll", false)]
  [InlineData("/v1/pairing/link-sessions", false)]
  [InlineData("/api/cloud-servers", false)]
  [InlineData(null, false)]
  public void CarriesSecretInPath_KeepsLinkCodeCallsOutOfTraces(string? path, bool expected) =>
    Microsoft.Extensions.Hosting.Extensions.CarriesSecretInPath(path).Should().Be(expected);

  private sealed class CapturingLogger<T> : ILogger<T>
  {
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Lines.Add(formatter(state, exception));
  }

  private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
  }

  private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
  }

  private static (CloudPairingPortalClient Client, CapturingLogger<CloudPairingPortalClient> Log) Portal(Func<HttpResponseMessage> respond)
  {
    var options = Options.Create(new CloudPortalOptions { PairingUrl = "http://127.0.0.1:5201", OwnerJwtKey = CloudOwnerTokenIssuerTests.Key });
    var log = new CapturingLogger<CloudPairingPortalClient>();
    return (new CloudPairingPortalClient(new Factory(new Handler(respond)), options, new CloudOwnerTokenIssuer(options), log), log);
  }

  [Fact]
  public async Task PortalClient_ACloudFailureOnALinkCode_LogsTheRedactedPath()
  {
    var (client, log) = Portal(() => new HttpResponseMessage(HttpStatusCode.BadGateway));

    await Assert.ThrowsAsync<CloudPortalException>(() => client.ApproveLinkSessionAsync("cus_1", "BCDF-GH23", mfa: true));

    log.Lines.Should().NotBeEmpty();
    log.Lines.Should().AllSatisfy(l => l.Should().NotContain("BCDF-GH23"));
    log.Lines.Should().Contain(l => l.Contains("link-sessions/[code]/approve"));
  }

  [Fact]
  public async Task PortalClient_ARefusedTokenOnALinkCode_LogsTheRedactedPath()
  {
    var (client, log) = Portal(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));

    await Assert.ThrowsAsync<CloudPortalException>(() => client.GetLinkSessionAsync("cus_1", "BCDF-GH23"));

    log.Lines.Should().ContainSingle().Which.Should().NotContain("BCDF-GH23").And.Contain("link-sessions/[code]");
  }

  [Fact]
  public async Task PortalClient_AnUnreachableCloud_LogsTheRedactedPath()
  {
    var (client, log) = Portal(() => throw new HttpRequestException("connection refused"));

    await Assert.ThrowsAsync<CloudPortalException>(() => client.DenyLinkSessionAsync("cus_1", "BCDF-GH23"));

    log.Lines.Should().ContainSingle().Which.Should().NotContain("BCDF-GH23");
  }
}
