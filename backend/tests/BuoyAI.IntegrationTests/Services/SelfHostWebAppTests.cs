using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using BuoyAI.API.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BuoyAI.IntegrationTests.Services;

/// <summary>
/// Kestrel serving the Flutter build in the self-host edition: base path, SPA fallback, cache and
/// security headers, precompressed files. Runs the real middleware against a fake build folder.
/// </summary>
public sealed class SelfHostWebAppTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bm-web-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html>shell</html>");
        File.WriteAllText(Path.Combine(_root, "main.dart.js"), "console.log('app');");
        File.WriteAllBytes(Path.Combine(_root, "main.dart.js.gz"), Gzip("console.log('app');"));
        File.WriteAllText(Path.Combine(_root, "assets", "data.json"), "{\"a\":1}");
        File.WriteAllBytes(Path.Combine(_root, "assets", "blob.bin"), new byte[] { 1, 2, 3 });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.UseSelfHostWebApp(_root); // also adds routing
        _app.MapGet("/api/ping", () => "pong");
        _app.MapSelfHostWebApp(_root);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(System.Text.Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    [Fact]
    public async Task RootRedirectsToTheApp_AndBareAppGetsATrailingSlash()
    {
        var root = await _client.GetAsync("/");
        root.StatusCode.Should().Be(HttpStatusCode.Redirect);
        root.Headers.Location!.OriginalString.Should().Be("/app/");

        var bare = await _client.GetAsync("/app");
        bare.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        bare.Headers.Location!.OriginalString.Should().Be("/app/");
    }

    [Theory]
    [InlineData("/app/")]
    [InlineData("/app/index.html")]
    [InlineData("/app/budgets")]
    [InlineData("/app/settings/profile/deep/link")]
    public async Task TheAppShellIsServedForTheFolderAndForEveryClientSideRoute(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("<html>shell</html>");
        response.Headers.CacheControl!.NoCache.Should().BeTrue("an upgrade must be picked up at once");
    }

    [Fact]
    public async Task RealFilesAreServedWithTheirOwnTypeAndRevalidatedByETag()
    {
        var response = await _client.GetAsync("/app/assets/data.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        var etag = response.Headers.ETag!;

        var again = new HttpRequestMessage(HttpMethod.Get, "/app/assets/data.json");
        again.Headers.IfNoneMatch.Add(etag);
        (await _client.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.NotModified);

        var unknown = await _client.GetAsync("/app/assets/blob.bin");
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        unknown.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task SecurityHeadersAreOnEveryAppResponse_AndTheContentPolicyLoadsNothingFromOutside()
    {
        foreach (var path in new[] { "/app/", "/app/main.dart.js", "/app/some/route" })
        {
            var response = await _client.GetAsync(path);
            response.Headers.GetValues("X-Content-Type-Options").Should().Contain("nosniff");
            response.Headers.GetValues("X-Frame-Options").Should().Contain("SAMEORIGIN");
            response.Headers.GetValues("X-Robots-Tag").Should().Contain("noindex, nofollow");
            var csp = response.Headers.GetValues("Content-Security-Policy").Single();
            csp.Should().Contain("default-src 'self'").And.Contain("object-src 'none'")
                .And.NotContain("accounts.google.com").And.NotContain("plaid").And.NotContain("stripe")
                .And.NotContain("posthog").And.NotContain("sentry");
            response.Headers.Contains("Strict-Transport-Security").Should().BeFalse("plain HTTP on a LAN must not be pinned to HTTPS");
        }
    }

    [Fact]
    public async Task HstsIsSentOnlyWhenTheRequestArrivedOverHttps()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost/app/");

        var response = await _client.SendAsync(request);

        response.Headers.GetValues("Strict-Transport-Security").Should().ContainSingle();
    }

    [Fact]
    public async Task APrecompressedFileIsServedAsIsToAClientThatTakesGzip()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/main.dart.js");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.Should().Contain("gzip");
        response.Content.Headers.ContentType!.MediaType.Should().BeOneOf("text/javascript", "application/javascript");
        response.Headers.Vary.Should().Contain("Accept-Encoding");
        // The body on the wire is the gzip file the build shipped; unzipped it is the script.
        await using var unzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(unzip);
        (await reader.ReadToEndAsync()).Should().Be("console.log('app');");
    }

    [Fact]
    public async Task AClientThatRefusesGzipWithQualityZeroGetsThePlainFile()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/main.dart.js");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip", 0));

        var response = await _client.SendAsync(request);

        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        (await response.Content.ReadAsStringAsync()).Should().Be("console.log('app');");
    }

    [Fact]
    public async Task AClientWithoutGzipGetsThePlainFile()
    {
        var response = await _client.GetAsync("/app/main.dart.js");

        response.Content.Headers.ContentEncoding.Should().BeEmpty();
        (await response.Content.ReadAsStringAsync()).Should().Be("console.log('app');");
    }

    [Fact]
    public async Task ApiRoutesAreLeftAlone()
    {
        var ok = await _client.GetAsync("/api/ping");
        (await ok.Content.ReadAsStringAsync()).Should().Be("pong");
        ok.Headers.Contains("Content-Security-Policy").Should().BeFalse("the app policy is for /app/ only");

        (await _client.GetAsync("/api/not-a-route")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ADotDotPathCannotEscapeTheWebRoot()
    {
        var response = await _client.GetAsync("/app/..%2f..%2fappsettings.json");

        (await response.Content.ReadAsStringAsync()).Should().Be("<html>shell</html>");
    }

    [Fact]
    public void ResolveWebRoot_NeedsAnIndexHtml()
    {
        var empty = Path.Combine(_root, "assets");
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SelfHost:WebRoot"] = empty }).Build();
        SelfHostWebApp.ResolveWebRoot(config).Should().BeNull();

        var good = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SelfHost:WebRoot"] = _root }).Build();
        SelfHostWebApp.ResolveWebRoot(good).Should().Be(_root);
    }
}
