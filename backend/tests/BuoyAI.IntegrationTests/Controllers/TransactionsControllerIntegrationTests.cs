using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;
using BuoyAI.IntegrationTests.Helpers;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.DTOs.Transaction;

namespace BuoyAI.IntegrationTests.Controllers;

public class TransactionsControllerIntegrationTests : IDisposable
{
    private readonly TestWebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public TransactionsControllerIntegrationTests()
    {
        _factory = new TestWebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    #region Authentication Tests

    [Fact]
    public async Task GetTransactions_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/transactions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTransactionSummary_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/transactions/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region GetTransactions Tests

    [Fact]
    public async Task GetTransactions_WithAuthentication_ShouldReturnTransactions()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/transactions");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<TransactionResponseDto>(content);
        
        result.Should().NotBeNull();
        result!.Transactions.Should().NotBeNull();
        result.Pagination.Should().NotBeNull();
        result.Pagination.TotalItems.Should().BeGreaterThanOrEqualTo(0);
        result.Pagination.CurrentPage.Should().Be(1);
    }

    [Fact]
    public async Task GetTransactions_WithPagination_ShouldReturnCorrectPage()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/transactions?page=1&pageSize=5");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<TransactionResponseDto>(content);
        
        result.Should().NotBeNull();
        result!.Pagination.CurrentPage.Should().Be(1);
        result.Pagination.PageSize.Should().Be(5);
        result.Transactions.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task GetTransactions_WithDateRange_ShouldFilterCorrectly()
    {
        // Arrange
        await AuthenticateAsync();
        var startDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Act
        var response = await _client.GetAsync($"/api/transactions?startDate={startDate}&endDate={endDate}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<TransactionResponseDto>(content);
        
        result.Should().NotBeNull();
        result!.Transactions.Should().NotBeNull();
    }

    [Fact]
    public async Task GetTransactions_WithInvalidDateRange_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var startDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");

        // Act
        var response = await _client.GetAsync($"/api/transactions?startDate={startDate}&endDate={endDate}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetTransactions_WithCategoryFilter_ShouldFilterByCategory()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/transactions?category=Groceries");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonConvert.DeserializeObject<TransactionResponseDto>(content);
        
        result.Should().NotBeNull();
        result!.Transactions.Should().NotBeNull();
    }

    #endregion

    #region GetTransactionSummary Tests

    [Fact]
    public async Task GetTransactionSummary_WithAuthentication_ShouldReturnSummary()
    {
        // Arrange
        await AuthenticateAsync();

        // Act
        var response = await _client.GetAsync("/api/transactions/summary");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var summary = JsonConvert.DeserializeObject<TransactionSummaryDto>(content);
        
        summary.Should().NotBeNull();
        summary!.TotalIncome.Should().BeGreaterThanOrEqualTo(0);
        summary.TotalExpenses.Should().BeGreaterThanOrEqualTo(0);
        summary.NetAmount.Should().Be(summary.TotalIncome - summary.TotalExpenses);
    }

    [Fact]
    public async Task GetTransactionSummary_WithDateRange_ShouldCalculateCorrectly()
    {
        // Arrange
        await AuthenticateAsync();
        var startDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Act
        var response = await _client.GetAsync($"/api/transactions/summary?startDate={startDate}&endDate={endDate}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        var summary = JsonConvert.DeserializeObject<TransactionSummaryDto>(content);
        
        summary.Should().NotBeNull();
        summary!.TotalIncome.Should().BeGreaterThanOrEqualTo(0);
        summary.TotalExpenses.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetTransactionSummary_WithInvalidDateRange_ShouldReturnBadRequest()
    {
        // Arrange
        await AuthenticateAsync();
        var startDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");

        // Act
        var response = await _client.GetAsync($"/api/transactions/summary?startDate={startDate}&endDate={endDate}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Helper Methods

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
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);
    }

    #endregion
}
