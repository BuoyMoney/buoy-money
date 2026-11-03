using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Core.Entities;
using BuoyAI.IntegrationTests.Helpers;
using System.Net;

namespace BuoyAI.IntegrationTests.Controllers;

[Collection("IntegrationTest")]
public class DataRetentionControllerIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public DataRetentionControllerIntegrationTests(TestWebApplicationFactory<Program> factory)
    {
        // Create a unique database name for this test instance
        var uniqueDbName = $"TestDatabase_{Guid.NewGuid()}";
        
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove the existing DbContext registration
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<BuoyAIDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                // Add in-memory database with unique name for this test instance
                services.AddDbContext<BuoyAIDbContext>(options =>
                {
                    options.UseInMemoryDatabase(uniqueDbName);
                });
            });
        });
        
        _client = _factory.CreateClient();
    }

    /// <summary>
    /// The JWT hook rejects tokens whose user row is missing, so any test that mints a token
    /// for user <paramref name="id"/> without otherwise creating it must seed the row first.
    /// </summary>
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

    [Fact]
    public async Task DeleteUserAccount_ShouldDeleteAllUserData_WhenUserExists()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

        // Create test user
        var user = new User
        {
            Id = 1,
            Email = "test@example.com",
            PasswordHash = "hashed_password",
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(user);

        // Create associated data
        var plaidConnection = new PlaidConnection
        {
            Id = 1,
            UserId = 1,
            AccessToken = "encrypted_token",
            ItemId = "item123",
            InstitutionId = "test_institution",
            InstitutionName = "Test Bank",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastSyncAt = DateTime.UtcNow
        };
        context.PlaidConnections.Add(plaidConnection);

        var aiInsight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Test insight",
            Message = "You're spending a lot on coffee",
            Priority = "medium",
            CreatedAt = DateTime.UtcNow,
            GeneratedAt = DateTime.UtcNow
        };
        context.AIInsights.Add(aiInsight);

        await context.SaveChangesAsync();

        // Get JWT token for authentication
        var token = TestAuthenticationHelper.GenerateJwtToken("1", "test@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.DeleteAsync("/api/dataretention/account");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("permanently deleted");

        // Verify user is marked as deleted - create new scope to see latest changes
        using var verificationScope = _factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        
        var updatedUser = await verificationContext.Users.FindAsync(1);
        updatedUser.Should().NotBeNull();
        updatedUser!.IsDeleted.Should().BeTrue();
        updatedUser.DeletedAt.Should().NotBeNull();
        updatedUser.DeletionReason.Should().Be("immediate_request");

        // Verify associated data is deleted
        var remainingConnections = await verificationContext.PlaidConnections.Where(t => t.UserId == 1).ToListAsync();
        remainingConnections.Should().BeEmpty();

        var remainingInsights = await verificationContext.AIInsights.Where(i => i.UserId == 1).ToListAsync();
        remainingInsights.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestDataDeletion_ShouldLogRequest_WhenValidRequest()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

        var user = new User
        {
            Id = 2,
            Email = "test2@example.com",
            PasswordHash = "hashed_password",
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var token = TestAuthenticationHelper.GenerateJwtToken("2", "test2@example.com");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            DataTypes = new[] { "transactions", "insights" },
            Reason = "Privacy concern"
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json"
        );

        // Act
        var response = await _client.PostAsync("/api/dataretention/delete-request", jsonContent);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("deletion request received");

        // Verify audit log was created - create new scope to see latest changes
        using var verificationScope = _factory.Services.CreateScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
        
        var auditLogs = await verificationContext.DataRetentionAuditLogs
            .Where(log => log.UserId == 2 && log.RequestSource == "user_request" && log.DeletionType == "partial_deletion")
            .ToListAsync();
        auditLogs.Should().HaveCount(1);
    }

    [Fact]
    public async Task TriggerManualCleanup_ShouldRequireAdminRole()
    {
        // Arrange
        await SeedUserAsync(1, "test@example.com");
        var token = TestAuthenticationHelper.GenerateJwtToken("1", "test@example.com", "User");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.PostAsync("/api/dataretention/cleanup", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TriggerManualCleanup_ShouldExecuteCleanup_WhenAdminUser()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

        // Create old closed user account (older than 30 days)
        var oldClosedUser = new User
        {
            Id = 100,
            Email = "old@example.com",
            PasswordHash = "hashed_password",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-35),
            DeletionReason = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-40)
        };
        context.Users.Add(oldClosedUser);

        // Create old security logs (older than 90 days)
        var oldSecurityLog = new SecurityAuditLog
        {
            Id = 1,
            UserId = 1,
            EventType = "login_attempt",
            Description = "Test login attempt",
            IpAddress = "192.168.1.1",
            UserAgent = "Test Browser",
            CreatedAt = DateTime.UtcNow.AddDays(-95)
        };
        context.SecurityAuditLogs.Add(oldSecurityLog);

        // Create old error logs (older than 365 days)
        var oldErrorLog = new ErrorLog
        {
            Id = 1,
            Message = "Test error",
            StackTrace = "Stack trace",
            ErrorType = "Error",
            CreatedAt = DateTime.UtcNow.AddDays(-370)
        };
        context.ErrorLogs.Add(oldErrorLog);

        await context.SaveChangesAsync();

        await SeedUserAsync(1, "admin@example.com");
        var adminToken = TestAuthenticationHelper.GenerateJwtToken("1", "admin@example.com", "Admin");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Act
        var response = await _client.PostAsync("/api/dataretention/cleanup", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        var cleanupResult = JsonSerializer.Deserialize<JsonElement>(responseContent);

        cleanupResult.GetProperty("message").GetString().Should().Contain("completed successfully");

        // Verify old data was cleaned up
        var remainingClosedUsers = await context.Users
            .Where(u => u.IsDeleted && u.DeletedAt < DateTime.UtcNow.AddDays(-30))
            .ToListAsync();
        remainingClosedUsers.Should().BeEmpty();

        var remainingSecurityLogs = await context.SecurityAuditLogs
            .Where(log => log.CreatedAt < DateTime.UtcNow.AddDays(-90))
            .ToListAsync();
        remainingSecurityLogs.Should().BeEmpty();

        var remainingErrorLogs = await context.ErrorLogs
            .Where(log => log.CreatedAt < DateTime.UtcNow.AddDays(-365))
            .ToListAsync();
        remainingErrorLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAuditLogs_ShouldRequireAdminRole()
    {
        // Arrange
        await SeedUserAsync(1, "test@example.com");
        var token = TestAuthenticationHelper.GenerateJwtToken("1", "test@example.com", "User");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Act
        var response = await _client.GetAsync("/api/dataretention/audit-logs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAuditLogs_ShouldReturnLogs_WhenAdminUser()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

        var auditLog = new DataRetentionAuditLog
        {
            Id = Guid.NewGuid(),
            UserId = 1,
            OperationType = "delete_user_data",
            DeletionType = "user_account",
            Summary = "Manual deletion by user request - 1 record",
            ExecutedAt = DateTime.UtcNow.AddDays(-1),
            ExecutedBy = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        context.DataRetentionAuditLogs.Add(auditLog);
        await context.SaveChangesAsync();

        await SeedUserAsync(1, "admin@example.com");
        var adminToken = TestAuthenticationHelper.GenerateJwtToken("1", "admin@example.com", "Admin");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Act
        var response = await _client.GetAsync("/api/dataretention/audit-logs");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("delete_user_data");
    }

    [Fact]
    public async Task DeleteUserAccount_ShouldReturnUnauthorized_WhenNoToken()
    {
        // Act
        var response = await _client.DeleteAsync("/api/dataretention/account");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
