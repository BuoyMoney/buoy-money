using System.Net;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>
/// #941 country plumbing: registration from a sanctioned country is refused (403),
/// a resolvable country is stamped onto the new account (SignupCountry), and a geo
/// outage FAILS OPEN — geolocation being down must never block signups.
/// </summary>
internal sealed class FakeClientIpStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
  public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
    Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
  {
    app.Use(async (ctx, nextMiddleware) =>
    {
      ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
      await nextMiddleware();
    });
    next(app);
  };
}

[Collection("IntegrationTest")]
public class GeoGateIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _baseFactory;

  public GeoGateIntegrationTests(TestWebApplicationFactory<Program> factory)
  {
    _baseFactory = factory;
  }

  private (HttpClient Client, WebApplicationFactory<Program> Factory) CreateClient(GeoLocationDto? location)
  {
    var uniqueDbName = $"TestDatabase_{Guid.NewGuid()}";
    var factory = _baseFactory.WithWebHostBuilder(builder =>
    {
      builder.ConfigureServices(services =>
      {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BuoyAIDbContext>));
        if (descriptor != null) services.Remove(descriptor);
        // Registration opens a real transaction (execution strategy); the InMemory
        // provider throws on that by default, so ignore the transaction warning.
        services.AddDbContext<BuoyAIDbContext>(options => options
          .UseInMemoryDatabase(uniqueDbName)
          .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddDbContextFactory<BuoyAIDbContext>(options => options
          .UseInMemoryDatabase(uniqueDbName)
          .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)),
          ServiceLifetime.Scoped);

        var geoDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IIpGeolocationService));
        if (geoDescriptor != null) services.Remove(geoDescriptor);
        var geo = new Mock<IIpGeolocationService>();
        if (location == null)
        {
          geo.Setup(g => g.GetLocationAsync(It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("geo service down"));
        }
        else
        {
          geo.Setup(g => g.GetLocationAsync(It.IsAny<string>())).ReturnsAsync(location);
        }
        services.AddSingleton(geo.Object);

        // TestServer requests carry no RemoteIpAddress, which makes the gate
        // short-circuit (fail open) before ever consulting geolocation. Stamp a
        // documentation-range IP on every request so the gate actually runs.
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new FakeClientIpStartupFilter());
      });
    });
    return (factory.CreateClient(), factory);
  }

  private static StringContent RegisterBody(string email, string? signupContext = null) => new(
    JsonSerializer.Serialize(new
    {
      email,
      password = "Str0ng!Passw0rd42",
      firstName = "Geo",
      lastName = "Tester",
      signupContext
    }), Encoding.UTF8, "application/json");

  // Cloud-only sign-up through the real endpoint: the request field binds, the flag is stored, and no trial is granted.
  [Fact]
  public async Task Register_FromTheCloudPortal_IsCloudOnly_WithNoTrial()
  {
    var (client, factory) = CreateClient(new GeoLocationDto { CountryCode = "US", Country = "United States" });

    var response = await client.PostAsync("/api/auth/register", RegisterBody("cloud-portal@example.com", "cloud"));

    response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    using var scope = factory.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var user = await context.Users.AsNoTracking().SingleAsync(u => u.Email == "cloud-portal@example.com");
    user.CloudOnly.Should().BeTrue();
    user.IsTrialActive.Should().BeFalse();
    user.HasUsedTrial.Should().BeFalse();
  }

  [Fact]
  public async Task Register_Normally_IsNotCloudOnly_AndGetsTheTrial()
  {
    var (client, factory) = CreateClient(new GeoLocationDto { CountryCode = "US", Country = "United States" });

    var response = await client.PostAsync("/api/auth/register", RegisterBody("hosted-normal@example.com"));

    response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    using var scope = factory.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var user = await context.Users.AsNoTracking().SingleAsync(u => u.Email == "hosted-normal@example.com");
    user.CloudOnly.Should().BeFalse();
    user.IsTrialActive.Should().BeTrue();
  }

  [Fact]
  public async Task Register_FromSanctionedCountry_IsRefused()
  {
    var (client, factory) = CreateClient(new GeoLocationDto { CountryCode = "IR", Country = "Iran" });

    var response = await client.PostAsync("/api/auth/register", RegisterBody("geo-blocked@example.com"));

    response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    (await response.Content.ReadAsStringAsync()).Should().Contain("not available in your region");

    using var scope = factory.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    (await context.Users.AnyAsync(u => u.Email == "geo-blocked@example.com")).Should().BeFalse();
  }

  [Fact]
  public async Task Register_StampsSignupCountry()
  {
    var (client, factory) = CreateClient(new GeoLocationDto { CountryCode = "CA", Country = "Canada" });

    var response = await client.PostAsync("/api/auth/register", RegisterBody("geo-ca@example.com"));

    response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    using var scope = factory.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var user = await context.Users.AsNoTracking().SingleAsync(u => u.Email == "geo-ca@example.com");
    user.SignupCountry.Should().Be("CA");
  }

  [Fact]
  public async Task Register_GeoOutage_FailsOpen()
  {
    var (client, factory) = CreateClient(location: null);

    var response = await client.PostAsync("/api/auth/register", RegisterBody("geo-outage@example.com"));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var scope = factory.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var user = await context.Users.AsNoTracking().SingleAsync(u => u.Email == "geo-outage@example.com");
    user.SignupCountry.Should().BeNull();
  }
}
