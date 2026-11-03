using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

public class NotificationsControllerIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public NotificationsControllerIntegrationTests()
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
        var loginRequest = new LoginRequest
        {
            Email = "test@BuoyAI.com",
            Password = "password123"
        };

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        loginResponse.EnsureSuccessStatusCode();

        var loginContent = await loginResponse.Content.ReadAsStringAsync();
        var loginResult = JsonConvert.DeserializeObject<LoginResponse>(loginContent);

        _client.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", loginResult!.Token);
    }

    #region Authentication Tests

    [Fact]
    public async Task GetNotifications_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetNotificationSummary_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/notifications/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region GetNotifications Tests

    [Fact]
    public async Task GetNotifications_WithAuthentication_ShouldReturnNotifications()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/notifications");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<List<NotificationDto>>(content);
        
        result.Should().NotBeNull();
        result.Should().BeOfType<List<NotificationDto>>();
    }

    [Fact]
    public async Task GetNotifications_WithPagination_ShouldRespectPageSize()
    {
        // Arrange
        await AuthenticateAsync();
        
        // First create some test notifications
        await _client.PostAsync("/api/notifications/create-test", null);

        // Act
        var response = await _client.GetAsync("/api/notifications?page=1&pageSize=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<List<NotificationDto>>(content);
        
        result.Should().NotBeNull();
        result!.Count.Should().BeLessOrEqualTo(2);
    }

    #endregion

    #region GetNotificationsPaginated Tests

    [Fact]
    public async Task GetNotificationsPaginated_ShouldReturnPaginatedResponse()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create test notifications
        await _client.PostAsync("/api/notifications/create-test", null);

        // Act
        var response = await _client.GetAsync("/api/notifications/paginated?page=1&pageSize=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"notifications\"");
        content.Should().Contain("\"hasMore\"");
        content.Should().Contain("\"page\":1");
        content.Should().Contain("\"pageSize\":5");
    }

    [Fact]
    public async Task GetNotificationsPaginated_WithIncludeArchived_ShouldIncludeArchivedNotifications()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/notifications/paginated?page=1&pageSize=10&includeArchived=true");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"includeArchived\":true");
    }

    #endregion

    #region GetNotificationSummary Tests

    [Fact]
    public async Task GetNotificationSummary_WithAuthentication_ShouldReturnSummary()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create test notifications
        await _client.PostAsync("/api/notifications/create-test", null);

        // Act
        var response = await _client.GetAsync("/api/notifications/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<NotificationSummaryDto>(content);
        
        result.Should().NotBeNull();
        result!.TotalCount.Should().BeGreaterOrEqualTo(0);
        result.UnreadCount.Should().BeGreaterOrEqualTo(0);
        result.Recent.Should().NotBeNull();
    }

    #endregion

    #region GetUnreadCount Tests

    [Fact]
    public async Task GetUnreadCount_WithAuthentication_ShouldReturnCount()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create test notifications (they should be unread by default)
        await _client.PostAsync("/api/notifications/create-test", null);

        // Act
        var response = await _client.GetAsync("/api/notifications/unread-count");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<int>(content);
        
        result.Should().BeGreaterOrEqualTo(0);
    }

    #endregion

    #region MarkAsRead Tests

    [Fact]
    public async Task MarkAsRead_WithValidNotification_ShouldReturnSuccess()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create a test notification
        await _client.PostAsync("/api/notifications/create-test", null);
        
        // Get the notification to find its ID
        var getResponse = await _client.GetAsync("/api/notifications");
        var getContent = await getResponse.Content.ReadAsStringAsync();
        var notifications = JsonConvert.DeserializeObject<List<NotificationDto>>(getContent);
        var notificationId = notifications!.First().Id;

        // Act
        var response = await _client.PostAsync($"/api/notifications/{notificationId}/mark-read", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task MarkAsRead_WithInvalidNotification_ShouldReturnNotFound()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidNotificationId = 999999;

        // Act
        var response = await _client.PostAsync($"/api/notifications/{invalidNotificationId}/mark-read", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region MarkAllAsRead Tests

    [Fact]
    public async Task MarkAllAsRead_WithAuthentication_ShouldReturnSuccess()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create test notifications
        await _client.PostAsync("/api/notifications/create-test", null);

        // Act
        var response = await _client.PostAsync("/api/notifications/mark-all-read", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"success\":true");
    }

    [Fact]
    public async Task MarkAllAsRead_ShouldReduceUnreadCount()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create test notifications
        await _client.PostAsync("/api/notifications/create-test", null);
        
        // Get initial unread count
        var initialResponse = await _client.GetAsync("/api/notifications/unread-count");
        var initialContent = await initialResponse.Content.ReadAsStringAsync();
        var initialCount = JsonConvert.DeserializeObject<int>(initialContent);

        // Act
        await _client.PostAsync("/api/notifications/mark-all-read", null);

        // Assert
        var finalResponse = await _client.GetAsync("/api/notifications/unread-count");
        var finalContent = await finalResponse.Content.ReadAsStringAsync();
        var finalCount = JsonConvert.DeserializeObject<int>(finalContent);
        
        finalCount.Should().BeLessThan(initialCount);
    }

    #endregion

    #region CreateTestNotifications Tests

    [Fact]
    public async Task CreateTestNotifications_WithAuthentication_ShouldReturnSuccess()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.PostAsync("/api/notifications/create-test", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"success\":true");
    }

    #endregion
}
