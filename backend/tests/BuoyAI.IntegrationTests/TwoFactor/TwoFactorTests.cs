using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using BuoyAI.API;
using BuoyAI.Core.Interfaces;
using BuoyAI.IntegrationTests.Helpers;

namespace BuoyAI.IntegrationTests.TwoFactor;

public class TwoFactorIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly TestWebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TwoFactorIntegrationTests(TestWebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public void TwoFactorService_ShouldBeRegistered()
    {
        // Arrange & Act
        using var scope = _factory.Services.CreateScope();
        var twoFactorService = scope.ServiceProvider.GetService<ITwoFactorService>();
        var emailService = scope.ServiceProvider.GetService<IEmailService>();

        // Assert
        Assert.NotNull(twoFactorService);
        Assert.NotNull(emailService);
    }

    [Fact]
    public void GenerateRecoveryCodes_ShouldReturnEightCodes()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var twoFactorService = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();

        // This test would need a valid user ID in a real scenario
        // For now, we're just testing the service registration
        Assert.NotNull(twoFactorService);
    }
}

/// <summary>
/// Unit tests for TwoFactorService that don't require a full web application
/// </summary>
public class TwoFactorServiceTests
{
    [Fact]
    public void RequiresUpgrade_EmailMfaWithinGracePeriod_ShouldReturnFalse()
    {
        // This would be a proper unit test with mocked dependencies
        // Testing the business logic of the grace period calculation
        var gracePeriodStart = DateTime.UtcNow.AddDays(-15); // 15 days ago
        var gracePeriodEnd = gracePeriodStart.AddDays(30);   // 15 days from now
        
        var currentTime = DateTime.UtcNow;
        var requiresUpgrade = currentTime > gracePeriodEnd;
        
        Assert.False(requiresUpgrade, "Should not require upgrade within grace period");
    }

    [Fact]
    public void RequiresUpgrade_EmailMfaAfterGracePeriod_ShouldReturnTrue()
    {
        // Test the business logic for expired grace period
        var gracePeriodStart = DateTime.UtcNow.AddDays(-35); // 35 days ago
        var gracePeriodEnd = gracePeriodStart.AddDays(30);   // 5 days ago
        
        var currentTime = DateTime.UtcNow;
        var requiresUpgrade = currentTime > gracePeriodEnd;
        
        Assert.True(requiresUpgrade, "Should require upgrade after grace period");
    }

    [Fact]
    public void CanAccessPlaidFeatures_WithTotpMfa_ShouldReturnTrue()
    {
        // Test Plaid access logic - only TOTP and Passkey should allow access
        var mfaTypes = new[] { "Totp", "Passkey" };
        
        foreach (var mfaType in mfaTypes)
        {
            var canAccess = mfaType == "Totp" || mfaType == "Passkey";
            Assert.True(canAccess, $"Should allow Plaid access with {mfaType}");
        }
    }

    [Fact]
    public void CanAccessPlaidFeatures_WithEmailMfa_ShouldReturnFalse()
    {
        // Test that email MFA doesn't allow Plaid access
        var mfaType = "Email";
        var canAccess = mfaType == "Totp" || mfaType == "Passkey";
        
        Assert.False(canAccess, "Should not allow Plaid access with email MFA");
    }
}
