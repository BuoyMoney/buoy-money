using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

public class ExportControllerIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ExportControllerIntegrationTests()
    {
        _factory = new TestWebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task AuthenticateAsync()
    {
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "test@BuoyAI.com",
            Password = "password123"
        });
        loginResponse.EnsureSuccessStatusCode();

        var loginResult = JsonConvert.DeserializeObject<LoginResponse>(await loginResponse.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult!.Token);
    }

    [Theory]
    [InlineData("/api/export")]
    [InlineData("/api/export?format=json")]
    [InlineData("/api/export?format=csv")]
    public async Task Export_WithoutAuthentication_ReturnsUnauthorized(string url)
    {
        var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_Json_ReturnsAttachmentWithJsonContentType()
    {
        await AuthenticateAsync();

        var response = await _client.GetAsync("/api/export?format=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().MatchRegex(@"^buoy-export-\d{4}-\d{2}-\d{2}\.json$");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("user").GetProperty("email").GetString().Should().Be("test@BuoyAI.com");
    }

    [Fact]
    public async Task Export_Csv_ReturnsZipAttachment()
    {
        await AuthenticateAsync();

        var response = await _client.GetAsync("/api/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().MatchRegex(@"^buoy-export-\d{4}-\d{2}-\d{2}\.zip$");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        zip.Entries.Select(e => e.FullName).Should().Contain(new[] { "user.csv", "accounts.csv", "transactions.csv", "EXCLUDED.md" });
    }

    [Fact]
    public async Task Export_NoFormat_DefaultsToJson()
    {
        await AuthenticateAsync();

        var response = await _client.GetAsync("/api/export");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("pdf")]
    [InlineData("../etc")]
    public async Task Export_BadFormat_ReturnsBadRequest(string format)
    {
        await AuthenticateAsync();

        var response = await _client.GetAsync($"/api/export?format={Uri.EscapeDataString(format)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
