using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Xunit;
using BuoyAI.IntegrationTests.Helpers;
using BuoyAI.Core.DTOs.Auth;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Enums;

namespace BuoyAI.IntegrationTests.Controllers;

public class BudgetControllerIntegrationTests : IDisposable
{
  private readonly TestWebApplicationFactory<Program> _factory;
  private readonly HttpClient _client;

  public BudgetControllerIntegrationTests()
  {
    _factory = new TestWebApplicationFactory<Program>();
    _client = _factory.CreateClient();
  }

  public void Dispose()
  {
    _client?.Dispose();
    _factory?.Dispose();
  }

  [Fact]
  public async Task GetBudgets_WithoutAuthentication_ShouldReturnUnauthorized()
  {
    // Act
    var response = await _client.GetAsync("/api/budget");

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
  }

  [Fact]
  public async Task GetActiveBudget_WithAuthentication_ShouldReturnActiveBudget()
  {
    // Arrange - Login first
    await AuthenticateAsync();

    // Act
    var response = await _client.GetAsync("/api/budget/active");

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    
    var content = await response.Content.ReadAsStringAsync();
    var budget = JsonConvert.DeserializeObject<BudgetDto>(content);
    
    budget.Should().NotBeNull();
    budget!.IsActive.Should().BeTrue();
    // Should have at least 2 real categories (may have additional virtual ones from transactions)
    budget.Categories.Where(c => !c.IsVirtual).Should().HaveCountGreaterThanOrEqualTo(2);
    
    // The budget name should be either the seeded budget or a newly created one
    // depending on test execution order - this makes the test more resilient
    budget.Name.Should().BeOneOf("Test Monthly Budget", "New Test Budget");
  }

  [Fact]
  public async Task CreateBudget_WithValidData_ShouldCreateBudgetSuccessfully()
  {
    // Arrange - Login first
    await AuthenticateAsync();

    var createRequest = new CreateBudgetRequest
    {
      Name = "New Test Budget",
      Period = BudgetPeriod.Monthly,
      TotalIncome = 6000m,
      Categories = new List<CreateBudgetCategoryRequest>
      {
        new() { CategoryId = (int)TransactionCategory.Groceries, AllocatedAmount = 600m },
        new() { CategoryId = (int)TransactionCategory.Transportation, AllocatedAmount = 400m }
      }
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/budget", createRequest);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.Created);
    
    var content = await response.Content.ReadAsStringAsync();
    var createdBudget = JsonConvert.DeserializeObject<BudgetDto>(content);
    
    createdBudget.Should().NotBeNull();
    createdBudget!.Name.Should().Be("New Test Budget");
    createdBudget.TotalIncome.Should().Be(6000m);
    // Should have exactly 2 real categories (may have additional virtual ones from transactions)
    createdBudget.Categories.Where(c => !c.IsVirtual).Should().HaveCount(2);
  }

  [Fact]
  public async Task CreateBudget_WithInvalidData_ShouldReturnBadRequest()
  {
    // Arrange - Login first
    await AuthenticateAsync();

    var createRequest = new CreateBudgetRequest
    {
      Name = "", // Invalid - empty name
      Period = BudgetPeriod.Monthly,
      TotalIncome = 6000m,
      Categories = new List<CreateBudgetCategoryRequest>()
    };

    // Act
    var response = await _client.PostAsJsonAsync("/api/budget", createRequest);

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
  }

  [Fact]
  public async Task GetAllBudgets_WithAuthentication_ShouldReturnUserBudgets()
  {
    // Arrange - Login first
    await AuthenticateAsync();

    // Act
    var response = await _client.GetAsync("/api/budget");

    // Assert
    response.StatusCode.Should().Be(HttpStatusCode.OK);
    
    var content = await response.Content.ReadAsStringAsync();
    var budgets = JsonConvert.DeserializeObject<List<BudgetDto>>(content);
    
    budgets.Should().NotBeNull();
    budgets!.Should().HaveCountGreaterThan(0);
    budgets.Should().OnlyContain(b => b.Id > 0);
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
      new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);
  }
}
