using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Services;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BuoyAI.IntegrationTests.Helpers;

public class TestWebApplicationFactory<TStartup> : WebApplicationFactory<TStartup> where TStartup : class
{
  // Unique database name per factory instance to avoid cross-test contamination in parallel execution
  private readonly string _databaseName = $"TestDatabase_{Guid.NewGuid()}";

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    // Set environment variables FIRST so Program.cs can read them during
    // builder configuration (before Build() — ConfigureAppConfiguration
    // runs too late for values read during the builder phase).
    Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
    Environment.SetEnvironmentVariable("Jwt__Key", "letsagoitsameamarioyahookoopatroopagoombabowserdonkeykong");
    Environment.SetEnvironmentVariable("Jwt__Issuer", "BuoyAI");
    Environment.SetEnvironmentVariable("Jwt__Audience", "BuoyAI-Users");
    Environment.SetEnvironmentVariable("Jwt__ExpiryInMinutes", "60");
    Environment.SetEnvironmentVariable("Encryption__Key", "integration-test-32-char-key-here");
    Environment.SetEnvironmentVariable("Gemini__ApiKey", "test_gemini_api_key_for_integration_tests");
    // BuoyAI Connect (RFC #782). Set as environment variables (not just in-memory config) because
    // Program.cs reads Mcp:Enabled during the builder phase — before ConfigureAppConfiguration's
    // in-memory sources are applied (same reason Jwt__* are set here). This maps /mcp + the OAuth
    // discovery documents for the whole integration suite; no other test exercises those routes.
    Environment.SetEnvironmentVariable("Mcp__Enabled", "true");
    Environment.SetEnvironmentVariable("Mcp__Issuer", "https://connect.test");
    Environment.SetEnvironmentVariable("Mcp__ResourceUri", "https://connect.test/mcp");
    builder.UseEnvironment("Testing");
    
    // Configure the configuration before any services are registered
    builder.ConfigureAppConfiguration((context, configBuilder) =>
    {
      configBuilder.Sources.Clear();
      
      // Try to find appsettings.json in different possible locations
      var possiblePaths = new[]
      {
        "appsettings.json", // Current directory
        "../../../src/BuoyAI.API/appsettings.json", // Relative from test project
        "../../../../src/BuoyAI.API/appsettings.json", // Relative from bin folder
        Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
        Path.Combine(Directory.GetCurrentDirectory(), "../../../src/BuoyAI.API/appsettings.json")
      };

      var appsettingsPath = possiblePaths.FirstOrDefault(File.Exists);
      if (appsettingsPath != null)
      {
        configBuilder.AddJsonFile(appsettingsPath, optional: false);
        var developmentPath = appsettingsPath.Replace("appsettings.json", "appsettings.Development.json");
        configBuilder.AddJsonFile(developmentPath, optional: true);
      }
      else
      {
        // If no appsettings.json found, create minimal in-memory configuration
        var baseConfig = new Dictionary<string, string?>
        {
          {"Logging:LogLevel:Default", "Information"},
          {"Logging:LogLevel:Microsoft.AspNetCore", "Warning"},
          {"AllowedHosts", "*"}
        };
        configBuilder.AddInMemoryCollection(baseConfig);
      }
      
      configBuilder.AddEnvironmentVariables("BuoyAI_TEST_");

      // Get the Plaid test credentials from environment variables
      var plaidClientId = Environment.GetEnvironmentVariable("BuoyAI_TEST_PLAID_CLIENT_ID");
      var plaidSecretKey = Environment.GetEnvironmentVariable("BuoyAI_TEST_PLAID_SECRET_KEY");
      
      // Get Stripe test credentials from environment variables (optional for tests)
      var stripeSecretKey = Environment.GetEnvironmentVariable("BuoyAI_TEST_STRIPE_SECRET_KEY") 
                            ?? "sk_test_integration_test_key_placeholder";
      
      var inMemoryConfig = new Dictionary<string, string?>
      {
        ["Jwt:Key"] = "letsagoitsameamarioyahookoopatroopagoombabowserdonkeykong",
        ["Jwt:Issuer"] = "BuoyAI", 
        ["Jwt:Audience"] = "BuoyAI-Users",
        ["Jwt:ExpiryInMinutes"] = "60",
        ["Encryption:Key"] = "integration-test-32-char-key-here",
        ["Plaid:Environment"] = "sandbox",
        ["Plaid:ClientName"] = "BuoyAI Integration Tests",
        ["Plaid:CountryCodes:0"] = "US",
        ["Plaid:Products:0"] = "transactions",
        ["Plaid:Products:1"] = "accounts", 
        ["Plaid:Products:2"] = "identity",
        ["AI:Provider"] = "Mock",
        // Stripe configuration for integration tests
        ["Stripe:SecretKey"] = stripeSecretKey,
        ["Stripe:WebhookSecret"] = "whsec_test_integration_test_webhook_secret",
        ["Stripe:PriceIds:StandardMonthly"] = "price_test_standard_monthly",
        ["Stripe:PriceIds:StandardYearly"] = "price_test_standard_yearly",
        ["Stripe:PriceIds:PremiumMonthly"] = "price_test_premium_monthly",
        ["Stripe:PriceIds:PremiumYearly"] = "price_test_premium_yearly",
        // Gemini configuration for integration tests (mock key - actual API not called)
        ["Gemini:ApiKey"] = "test_gemini_api_key_for_integration_tests",
        // BuoyAI Connect (RFC #782) — mirror the builder-phase env vars into the post-build config.
        ["Mcp:Enabled"] = "true",
        ["Mcp:Issuer"] = "https://connect.test",
        ["Mcp:ResourceUri"] = "https://connect.test/mcp",
        // Suppress verbose logging in tests (hosted services, HTTPS port detection, etc.)
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:BuoyAI"] = "Warning"
      };

      // Use real Plaid credentials if available, otherwise use mock values
      if (!string.IsNullOrEmpty(plaidClientId) && !string.IsNullOrEmpty(plaidSecretKey))
      {
        inMemoryConfig["Plaid:ClientId"] = plaidClientId;
        inMemoryConfig["Plaid:SecretKey"] = plaidSecretKey;
      }
      else
      {
        inMemoryConfig["Plaid:ClientId"] = "test_client_id";
        inMemoryConfig["Plaid:SecretKey"] = "test_secret_key";
      }

      configBuilder.AddInMemoryCollection(inMemoryConfig);
    });

    builder.ConfigureServices(services =>
    {
      // The DbContext registration from AddInfrastructure is automatically skipped
      // when ASPNETCORE_ENVIRONMENT is "Testing" (set via UseEnvironment below)
      // We just need to add the InMemory database for testing
      services.AddDbContext<BuoyAIDbContext>(options =>
      {
        options.UseInMemoryDatabase(_databaseName);
      });

      // Mirror the production registration: RefreshTokenService resolves its context via
      // IDbContextFactory. Registered Scoped so it can consume the scoped options above.
      services.AddDbContextFactory<BuoyAIDbContext>(
        options => options.UseInMemoryDatabase(_databaseName),
        ServiceLifetime.Scoped);
    });

    // Use a separate PostConfigureServices to seed data AFTER the container is built
    builder.ConfigureTestServices(services =>
    {
      // Build service provider to seed data
      var serviceProvider = services.BuildServiceProvider();

      using var scope = serviceProvider.CreateScope();
      var dbContext = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      var logger = scope.ServiceProvider.GetRequiredService<ILogger<TestWebApplicationFactory<TStartup>>>();

      try
      {
        dbContext.Database.EnsureCreated();
        SeedTestData(dbContext);
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "An error occurred seeding the test database");
      }
    });
  }

  private static void SeedTestData(BuoyAIDbContext context)
  {
    if (context.Users.Any())
      return;

    // Create test user with explicit ID to avoid conflicts
    var testUser = new User
    {
      Username = "testuser",
      PasswordHash = AuthService.HashPassword("password123"),
      Email = "test@BuoyAI.com",
      FirstName = "Test",
      LastName = "User",
      Currency = "USD",
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    };

    context.Users.Add(testUser);
    context.SaveChanges(); // Save to get the generated ID

    // Create test account using the actual user ID
    var testAccount = new Account
    {
      UserId = testUser.Id,
      PlaidAccountId = "test-account-id",
      Name = "Test Checking",
      OfficialName = "Test Checking Account",
      Type = Core.Enums.AccountType.Checking,
      Subtype = "checking",
      CurrentBalance = 1000m,
      AvailableBalance = 1000m,
      Currency = "USD",
      IsActive = true,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    };

    context.Accounts.Add(testAccount);

    // Create test budget using the actual user ID
    var testBudget = new Budget
    {
      UserId = testUser.Id,
      Name = "Test Monthly Budget",
      Period = Core.Enums.BudgetPeriod.Monthly,
      CurrentPeriodStart = DateTime.UtcNow.Date,
      CurrentPeriodEnd = DateTime.UtcNow.Date.AddMonths(1),
      LastReset = DateTime.UtcNow.Date,
      AutoReset = true,
      TotalIncome = 5000m,
      TotalExpenses = 3000m,
      IsActive = true,
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow
    };

    context.Budgets.Add(testBudget);
    context.SaveChanges(); // Save to get generated ID

    // Create budget categories after budget is saved
    var budgetCategories = new List<BudgetCategory>
    {
      new()
      {
        BudgetId = testBudget.Id,
        CategoryId = (int)Core.Enums.TransactionCategory.Groceries,
        AllocatedAmount = 500m,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      },
      new()
      {
        BudgetId = testBudget.Id,
        CategoryId = (int)Core.Enums.TransactionCategory.DiningOut,
        AllocatedAmount = 300m,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      }
    };

    context.BudgetCategories.AddRange(budgetCategories);

    // Create test transactions
    var testTransactions = new List<Transaction>
    {
      new()
      {
        AccountId = testAccount.Id,
        PlaidTransactionId = "test-transaction-1",
        Amount = 50m,
        Type = Core.Enums.TransactionType.Debit,
        Description = "Test Grocery Store",
        MerchantName = "Test Grocery",
        CategoryId = (int)Core.Enums.TransactionCategory.Groceries,
        Date = DateTime.UtcNow.Date.AddDays(-1),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      },
      new()
      {
        AccountId = testAccount.Id,
        PlaidTransactionId = "test-transaction-2",
        Amount = 25m,
        Type = Core.Enums.TransactionType.Debit,
        Description = "Test Restaurant",
        MerchantName = "Test Restaurant",
        CategoryId = (int)Core.Enums.TransactionCategory.DiningOut,
        Date = DateTime.UtcNow.Date.AddDays(-2),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
      }
    };

    context.Transactions.AddRange(testTransactions);
    context.SaveChanges();
  }
}

public static class TestDataHelper
{
  public static string GetValidJwtToken(HttpClient client)
  {
    // This would typically involve making a login request and extracting the token
    // For now, we'll return a placeholder - in real tests you'd authenticate first
    return "test-jwt-token";
  }
}
