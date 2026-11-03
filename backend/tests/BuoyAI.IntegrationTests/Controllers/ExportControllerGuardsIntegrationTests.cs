using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuoyAI.API.Services;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// The guards around the export: the real step-up service for accounts with MFA, the shared rate
/// limit, and the concurrency gate. Kept apart from the happy-path tests so each test gets its own
/// host (rate-limit counters live in the host).
/// </summary>
public class ExportControllerGuardsIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory<Program> _baseFactory = new();
    private readonly Mock<IEmailService> _email = new();
    private WebApplicationFactoryHandle? _handle;

    private sealed record WebApplicationFactoryHandle(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, HttpClient Client);

    public void Dispose()
    {
        _handle?.Client.Dispose();
        _handle?.Factory.Dispose();
        _baseFactory.Dispose();
    }

    private WebApplicationFactoryHandle Host(Action<IServiceCollection>? configure = null)
    {
        var factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton(_email.Object);
            configure?.Invoke(services);
        }));
        _handle = new WebApplicationFactoryHandle(factory, factory.CreateClient());
        return _handle;
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "test@BuoyAI.com",
            Password = "password123",
        });
        login.EnsureSuccessStatusCode();
        var result = JsonConvert.DeserializeObject<LoginResponse>(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result!.Token);
    }

    private static async Task<int> EnableMfaAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == "test@BuoyAI.com");
        user.HasMfaEnabled = true;
        user.MfaType = MfaType.Totp;
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Export_AccountWithMfa_GetsMfaRequiredChallenge_ThenSucceedsWithAnOperationToken()
    {
        var host = Host();
        await LoginAsync(host.Client);
        var userId = await EnableMfaAsync(host.Factory);

        var challenge = await host.Client.GetAsync("/api/export?format=json");
        challenge.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await challenge.Content.ReadAsStringAsync()).Should().Contain("MfaRequired");

        string token;
        using (var scope = host.Factory.Services.CreateScope())
        {
            var stepUp = scope.ServiceProvider.GetRequiredService<IStepUpAuthService>();
            token = (await stepUp.GenerateOperationTokenAsync(userId, ProtectedOperation.ViewSensitiveFinancialData)).OperationToken!;
        }
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/export?format=json");
        request.Headers.Add("X-Operation-Token", token);
        var ok = await host.Client.SendAsync(request);

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        _email.Verify(e => e.SendDataExportedAsync("test@BuoyAI.com", It.IsAny<string>(), "json", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Export_RateLimit_AllowsAChallengePlusFiveExports_ThenReturns429()
    {
        var host = Host();
        await LoginAsync(host.Client);
        // No MFA on this account: six plain exports fit, the seventh is limited. The limit is 6,
        // not 3, so that an MFA challenge and its retry (two permits) leave room for real downloads.
        for (var i = 0; i < 6; i++)
        {
            (await host.Client.GetAsync("/api/export?format=json")).StatusCode.Should().Be(HttpStatusCode.OK, $"request {i + 1}");
        }

        var limited = await host.Client.GetAsync("/api/export?format=json");

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Export_WhenTheConcurrencyGateIsFull_Returns429WithRetryAfter()
    {
        var full = new ExportConcurrencyGate(1);
        using var held = full.TryEnter(-1); // someone else holds the only slot
        var host = Host(services =>
        {
            services.RemoveAll<ExportConcurrencyGate>();
            services.AddSingleton(full);
        });
        await LoginAsync(host.Client);

        var response = await host.Client.GetAsync("/api/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Export_WhenTheNoticeEmailFails_StillReturnsTheFile()
    {
        _email.Setup(e => e.SendDataExportedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new InvalidOperationException("no email provider configured"));
        var host = Host();
        await LoginAsync(host.Client);

        var response = await host.Client.GetAsync("/api/export?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(100);
    }
}
