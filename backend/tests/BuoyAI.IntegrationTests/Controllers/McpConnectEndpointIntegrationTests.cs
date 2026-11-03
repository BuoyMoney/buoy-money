using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// The public BuoyAI Connect MCP endpoint (RFC #782). Verifies the endpoint is mapped when the
/// feature flag is on and that an unauthenticated request is challenged per RFC 9728 — a 401 whose
/// WWW-Authenticate header points at the protected-resource metadata document. This is the discovery
/// anchor a compliant MCP client follows to find the authorization server, so it must not regress.
/// </summary>
public class McpConnectEndpointIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _factory;

  public McpConnectEndpointIntegrationTests(TestWebApplicationFactory<Program> factory)
  {
    _factory = factory;
  }

  [Fact]
  public async Task Mcp_Unauthenticated_Returns401_WithProtectedResourceMetadataChallenge()
  {
    var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
      AllowAutoRedirect = false
    });

    // A minimal JSON-RPC initialize with no Authorization header — must be rejected by the McpBearer
    // scheme before reaching any handler.
    var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
    {
      Content = new StringContent(
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}",
        System.Text.Encoding.UTF8, "application/json")
    };
    request.Headers.Accept.ParseAdd("application/json");
    request.Headers.Accept.ParseAdd("text/event-stream");

    var response = await client.SendAsync(request);

    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    response.Headers.TryGetValues("WWW-Authenticate", out var values).Should().BeTrue(
      "an MCP 401 must advertise the protected-resource metadata document");
    var header = string.Join(" ", values!);
    header.Should().Contain("Bearer");
    header.Should().Contain("resource_metadata=");
    header.Should().Contain("/.well-known/oauth-protected-resource/mcp");
  }
}
