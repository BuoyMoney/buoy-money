using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// App Store 5.1.2(i): every model-calling endpoint must refuse until the user has
/// explicitly granted AI data-sharing consent, and withdrawing must re-block.
/// </summary>
[Collection("IntegrationTest")]
public class AiConsentGateIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public AiConsentGateIntegrationTests(TestWebApplicationFactory<Program> factory)
    {
        var uniqueDbName = $"TestDatabase_{Guid.NewGuid()}";
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BuoyAIDbContext>));
                if (descriptor != null) services.Remove(descriptor);
                services.AddDbContext<BuoyAIDbContext>(options => options.UseInMemoryDatabase(uniqueDbName));
            });
        });
        _client = _factory.CreateClient();
    }

    private async Task SeedUserAsync(int id, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        if (await context.Users.FindAsync(id) != null) return;
        context.Users.Add(new User
        {
            Id = id,
            Email = email,
            PasswordHash = "hashed_password",
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task AiEndpoints_BlockUntilConsent_AndReblockAfterWithdrawal()
    {
        await SeedUserAsync(701, "consent@example.com");
        var token = TestAuthenticationHelper.GenerateJwtToken("701", "consent@example.com", "User");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Apple is the platform that mandates the opt-in; identify as the iOS app.
        _client.DefaultRequestHeaders.Add("X-Client-Type", "mobile-ios");

        // 1. No consent: the model-calling endpoint is refused with the typed error code.
        var blocked = await _client.PostAsync("/api/ai/chat", Json(new { message = "hi" }));
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("AiConsentRequired");

        // Chart generation is gated the same way.
        var chartBlocked = await _client.PostAsync("/api/visualizations/generate", Json(new { }));
        chartBlocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await chartBlocked.Content.ReadAsStringAsync()).Should().Contain("AiConsentRequired");

        // Profile exposes the (null) grant so the client can show the sheet pre-emptively.
        var profile = await _client.GetAsync("/api/user/profile");
        profile.StatusCode.Should().Be(HttpStatusCode.OK);
        (await profile.Content.ReadAsStringAsync()).Should().Contain("\"aiConsentGrantedAt\":null");

        // 2. Grant.
        var grant = await _client.PutAsync("/api/user/ai-consent", Json(new { granted = true }));
        grant.StatusCode.Should().Be(HttpStatusCode.OK);
        (await grant.Content.ReadAsStringAsync()).Should().NotContain("\"aiConsentGrantedAt\":null");

        // The consent gate no longer fires (the endpoint may still refuse for tier reasons;
        // what matters is that the refusal is no longer the consent code).
        var afterGrant = await _client.PostAsync("/api/ai/chat", Json(new { message = "hi" }));
        (await afterGrant.Content.ReadAsStringAsync()).Should().NotContain("AiConsentRequired");

        // 3. Withdraw: blocked again.
        var withdraw = await _client.PutAsync("/api/user/ai-consent", Json(new { granted = false }));
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK);
        (await withdraw.Content.ReadAsStringAsync()).Should().Contain("\"aiConsentGrantedAt\":null");

        var reblocked = await _client.PostAsync("/api/ai/chat", Json(new { message = "hi" }));
        reblocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reblocked.Content.ReadAsStringAsync()).Should().Contain("AiConsentRequired");
    }

    [Fact]
    public async Task UndecidedAndroidUser_IsAskedToo_NoImpliedGrant()
    {
        // Consent is per user, so an implied grant on Android used to carry over to iOS. That is
        // how App Review reached AI Chat on an iPad without seeing the sheet. Explicit everywhere.
        await SeedUserAsync(703, "android@example.com");
        var token = TestAuthenticationHelper.GenerateJwtToken("703", "android@example.com", "User");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.Add("X-Client-Type", "mobile-android");

        var res = await _client.PostAsync("/api/ai/chat", Json(new { message = "hi" }));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("AiConsentRequired");

        // Nothing was recorded on the user's behalf.
        var consent = await _client.GetAsync("/api/user/ai-consent");
        (await consent.Content.ReadAsStringAsync()).Should().Contain("\"aiConsentGrantedAt\":null");

        // An explicit grant opens it, and records the disclosure version it was given under.
        var grant = await _client.PutAsync("/api/user/ai-consent", Json(new { granted = true }));
        (await grant.Content.ReadAsStringAsync()).Should().Contain($"\"aiConsentVersion\":{AiConsent.CurrentVersion}");
        var allowed = await _client.PostAsync("/api/ai/chat", Json(new { message = "hi" }));
        (await allowed.Content.ReadAsStringAsync()).Should().NotContain("AiConsentRequired");
    }

    [Fact]
    public async Task ReadOnlyAiEndpoints_StayOpenWithoutConsent()
    {
        await SeedUserAsync(702, "reader@example.com");
        var token = TestAuthenticationHelper.GenerateJwtToken("702", "reader@example.com", "User");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // A user who declined can still see (and delete) what exists: listing endpoints are not gated.
        var prefs = await _client.GetAsync("/api/ai/preferences");
        prefs.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        var consent = await _client.GetAsync("/api/user/ai-consent");
        consent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await consent.Content.ReadAsStringAsync()).Should().Contain("\"aiConsentGrantedAt\":null");
    }
}
