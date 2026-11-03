using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.DTOs.Subscription;
using BuoyAI.Core.Enums;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

public class SubscriptionsControllerIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public SubscriptionsControllerIntegrationTests()
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
    public async Task GetSubscriptions_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/subscriptions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateSubscription_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new CreateSubscriptionRequest
        {
            Name = "Netflix",
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30)
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/subscriptions", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region CRUD Tests

    [Fact]
    public async Task GetSubscriptions_WithAuthentication_ShouldReturnSubscriptions()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/subscriptions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<List<SubscriptionDto>>(content);
        
        result.Should().NotBeNull();
        result.Should().BeOfType<List<SubscriptionDto>>();
    }

    [Fact]
    public async Task CreateSubscription_WithValidRequest_ShouldReturnCreated()
    {
        // Arrange
        await AuthenticateAsync();
        var request = new CreateSubscriptionRequest
        {
            Name = "Netflix",
            Description = "Streaming service",
            Amount = 15.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30),
            Category = SubscriptionCategory.Streaming,
            MerchantName = "Netflix Inc"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/subscriptions", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<SubscriptionDto>(content);
        
        result.Should().NotBeNull();
        result!.Name.Should().Be("Netflix");
        result.Amount.Should().Be(15.99m);
        result.Frequency.Should().Be(SubscriptionFrequency.Monthly);
    }

    [Fact]
    public async Task GetSubscription_WithValidId_ShouldReturnSubscription()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create a subscription first
        var createRequest = new CreateSubscriptionRequest
        {
            Name = "Spotify",
            Amount = 9.99m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(15),
            Category = SubscriptionCategory.Streaming
        };
        var createResponse = await _client.PostAsJsonAsync("/api/subscriptions", createRequest);
        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createdSubscription = JsonConvert.DeserializeObject<SubscriptionDto>(createContent);

        // Act
        var response = await _client.GetAsync($"/api/subscriptions/{createdSubscription!.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<SubscriptionDto>(content);
        
        result.Should().NotBeNull();
        result!.Id.Should().Be(createdSubscription.Id);
        result.Name.Should().Be("Spotify");
    }

    [Fact]
    public async Task GetSubscription_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidId = 999999;

        // Act
        var response = await _client.GetAsync($"/api/subscriptions/{invalidId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateSubscription_WithValidRequest_ShouldReturnUpdatedSubscription()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create a subscription first
        var createRequest = new CreateSubscriptionRequest
        {
            Name = "Original Name",
            Amount = 10.00m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30),
            Category = SubscriptionCategory.Other
        };
        var createResponse = await _client.PostAsJsonAsync("/api/subscriptions", createRequest);
        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createdSubscription = JsonConvert.DeserializeObject<SubscriptionDto>(createContent);

        // Update request
        var updateRequest = new UpdateSubscriptionRequest
        {
            Name = "Updated Name",
            Amount = 12.99m,
            Category = SubscriptionCategory.Streaming
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/subscriptions/{createdSubscription!.Id}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<SubscriptionDto>(content);
        
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
        result.Amount.Should().Be(12.99m);
    }

    [Fact]
    public async Task UpdateSubscription_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidId = 999999;
        var updateRequest = new UpdateSubscriptionRequest
        {
            Name = "Updated Name"
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/subscriptions/{invalidId}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteSubscription_WithValidId_ShouldReturnNoContent()
    {
        // Arrange
        await AuthenticateAsync();
        
        // Create a subscription first
        var createRequest = new CreateSubscriptionRequest
        {
            Name = "To Be Deleted",
            Amount = 5.00m,
            Frequency = SubscriptionFrequency.Monthly,
            NextDueDate = DateTime.UtcNow.AddDays(30),
            Category = SubscriptionCategory.Other
        };
        var createResponse = await _client.PostAsJsonAsync("/api/subscriptions", createRequest);
        var createContent = await createResponse.Content.ReadAsStringAsync();
        var createdSubscription = JsonConvert.DeserializeObject<SubscriptionDto>(createContent);

        // Act
        var response = await _client.DeleteAsync($"/api/subscriptions/{createdSubscription!.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSubscription_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        await AuthenticateAsync();
        var invalidId = 999999;

        // Act
        var response = await _client.DeleteAsync($"/api/subscriptions/{invalidId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Detection Tests

    [Fact]
    public async Task DetectSubscriptions_WithAuthentication_ShouldReturnDetections()
    {
        // Arrange
        await AuthenticateAsync();
        var request = new SubscriptionDetectionRequest
        {
            StartDate = DateTime.UtcNow.AddMonths(-6),
            EndDate = DateTime.UtcNow,
            MinOccurrences = 3,
            AmountTolerance = 3.00m
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/subscriptions/detect", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<SubscriptionDetectionResponse>(content);
        
        result.Should().NotBeNull();
        result!.DetectedSubscriptions.Should().NotBeNull();
    }

    [Fact]
    public async Task DetectSubscriptions_WithoutRequest_ShouldUseDefaults()
    {
        // Arrange
        await AuthenticateAsync();

        // Act - POST with null body
        var response = await _client.PostAsync("/api/subscriptions/detect", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<SubscriptionDetectionResponse>(content);
        
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ConfirmDetectedSubscription_WithValidData_ShouldReturnSubscription()
    {
        // Arrange
        await AuthenticateAsync();
        var detectedSubscription = new DetectedSubscriptionDto
        {
            MerchantName = "Apple Music",
            Amount = 10.99m,
            Frequency = SubscriptionFrequency.Monthly,
            EstimatedNextDue = DateTime.UtcNow.AddDays(30),
            Confidence = 0.95,
            IsSubscriptionLikely = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/subscriptions/confirm", detectedSubscription);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<SubscriptionDto>(content);
        
        result.Should().NotBeNull();
        result!.MerchantName.Should().Be("Apple Music");
        result.Amount.Should().Be(10.99m);
    }

    [Fact]
    public async Task DismissDetectedSubscription_WithValidId_ShouldReturnOk()
    {
        // Arrange
        await AuthenticateAsync();
        var dismissRequest = new DismissSubscriptionRequest
        {
            MerchantName = "Test Subscription",
            Amount = 9.99m
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/subscriptions/1/dismiss", dismissRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Calendar and Summary Tests

    [Fact]
    public async Task GetCalendarData_WithValidDates_ShouldReturnCalendarData()
    {
        // Arrange
        await AuthenticateAsync();
        var startDate = DateTime.UtcNow.Date;
        var endDate = startDate.AddMonths(1);

        // Act
        var response = await _client.GetAsync($"/api/subscriptions/calendar?startDate={startDate:yyyy-MM-dd}&endDate={endDate:yyyy-MM-dd}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<List<CalendarSubscriptionDto>>(content);
        
        result.Should().NotBeNull();
        result.Should().BeOfType<List<CalendarSubscriptionDto>>();
    }

    [Fact]
    public async Task GetSummary_WithAuthentication_ShouldReturnSummary()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/subscriptions/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<SubscriptionSummaryDto>(content);
        
        result.Should().NotBeNull();
        result!.MonthlyTotal.Should().BeGreaterOrEqualTo(0);
        result.YearlyTotal.Should().BeGreaterOrEqualTo(0);
        result.ActiveCount.Should().BeGreaterOrEqualTo(0);
        result.CategoryBreakdown.Should().NotBeNull();
    }

    #endregion
}
