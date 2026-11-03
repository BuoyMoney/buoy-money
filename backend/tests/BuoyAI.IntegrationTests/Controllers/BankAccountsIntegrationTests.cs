using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.IntegrationTests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuoyAI.IntegrationTests.Controllers;

/// <summary>Shared by the self-host and hosted runs: the Accounts page routes must behave the same in both.</summary>
internal static class BankAccountsTestData
{
  /// <summary>A user with one bank connection of the given provider holding one account and one transaction.</summary>
  public static async Task<(int UserId, int ConnectionId, int AccountId)> SeedAsync(IServiceProvider services, string email, BankProvider provider)
  {
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var now = DateTime.UtcNow;
    var user = new User
    {
      Email = email, Username = email.Split('@')[0], FirstName = "Home", LastName = "Owner",
      PasswordHash = "x", Currency = "USD", CreatedAt = now, UpdatedAt = now,
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    var connection = new PlaidConnection
    {
      UserId = user.Id, Provider = provider, ItemId = "item-" + Guid.NewGuid().ToString("N"), InstitutionId = "ins_1",
      InstitutionName = "Test Credit Union", AccessToken = "secret", LastSyncAt = now, CreatedAt = now, UpdatedAt = now,
    };
    db.PlaidConnections.Add(connection);
    await db.SaveChangesAsync();
    var account = new Account
    {
      UserId = user.Id, PlaidConnectionId = connection.Id, PlaidAccountId = "acct-" + Guid.NewGuid().ToString("N"),
      Name = "Checking", OfficialName = "Checking", Type = AccountType.Checking, Subtype = "checking",
      CurrentBalance = 125.50m, AvailableBalance = 100m, CreatedAt = now, UpdatedAt = now,
    };
    db.Accounts.Add(account);
    await db.SaveChangesAsync();
    db.Transactions.Add(new Transaction
    {
      AccountId = account.Id, PlaidTransactionId = "tx-" + Guid.NewGuid().ToString("N"),
      Amount = 12m, Description = "Coffee", Date = now, CreatedAt = now, UpdatedAt = now,
    });
    await db.SaveChangesAsync();
    return (user.Id, connection.Id, account.Id);
  }

  public static HttpRequestMessage As(int userId, HttpMethod method, string path, object? body = null)
  {
    var request = new HttpRequestMessage(method, path);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
      TestAuthenticationHelper.GenerateJwtToken(userId.ToString(), $"user{userId}@home.test"));
    if (body != null) request.Content = JsonContent.Create(body);
    return request;
  }
}

/// <summary>A self-hosted server answers the Accounts page, which is what the Plaid routes (404 there) could not do.</summary>
public class BankAccountsSelfHostIntegrationTests : IClassFixture<SelfHostFactory>
{
  private readonly SelfHostFactory _factory;

  public BankAccountsSelfHostIntegrationTests(SelfHostFactory factory) => _factory = factory;

  private HttpClient Client() => _factory.CreateClient(new() { AllowAutoRedirect = false });

  [Fact]
  public async Task TheRoutesNeedASignIn()
  {
    foreach (var path in new[] { "/api/bank/accounts", "/api/bank/connections", "/api/bank/providers" })
    {
      (await Client().GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
    }
  }

  [Fact]
  public async Task Connections_ListASimpleFinBank_WithItsAccountsAndTransactionCount()
  {
    var (userId, connectionId, accountId) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf1@home.test", BankProvider.SimpleFin);

    var response = await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, "/api/bank/connections"));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var connection = json.RootElement.EnumerateArray().Should().ContainSingle().Subject;
    connection.GetProperty("id").GetInt32().Should().Be(connectionId);
    connection.GetProperty("provider").GetString().Should().Be("SimpleFin");
    connection.GetProperty("institutionName").GetString().Should().Be("Test Credit Union");
    connection.GetProperty("totalTransactions").GetInt32().Should().Be(1);
    var account = connection.GetProperty("accounts").EnumerateArray().Should().ContainSingle().Subject;
    account.GetProperty("id").GetInt32().Should().Be(accountId);
    account.GetProperty("currentBalance").GetDecimal().Should().Be(125.50m);
  }

  [Fact]
  public async Task Accounts_ListTheUsersAccounts_AndNobodyElses()
  {
    var (userId, _, accountId) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf2@home.test", BankProvider.SimpleFin);
    await BankAccountsTestData.SeedAsync(_factory.Services, "sf3@home.test", BankProvider.SimpleFin);

    var response = await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, "/api/bank/accounts"));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    json.RootElement.EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).Should().Equal(accountId);
  }

  [Fact]
  public async Task Nickname_CanBeSetAndCleared_ByTheOwnerOnly()
  {
    var (userId, _, accountId) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf4@home.test", BankProvider.SimpleFin);
    var (otherId, _, _) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf5@home.test", BankProvider.SimpleFin);

    var set = await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Put, $"/api/bank/accounts/{accountId}/nickname", new { nickname = "  Joint  " }));
    set.StatusCode.Should().Be(HttpStatusCode.OK);
    using (var json = JsonDocument.Parse(await set.Content.ReadAsStringAsync()))
    {
      json.RootElement.GetProperty("nickname").GetString().Should().Be("Joint");
      json.RootElement.GetProperty("displayName").GetString().Should().Be("Joint");
    }

    var stranger = await Client().SendAsync(BankAccountsTestData.As(otherId, HttpMethod.Put, $"/api/bank/accounts/{accountId}/nickname", new { nickname = "Mine" }));
    stranger.StatusCode.Should().Be(HttpStatusCode.Forbidden);

    var missing = await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Put, "/api/bank/accounts/999999/nickname", new { nickname = "x" }));
    missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

    var cleared = await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Put, $"/api/bank/accounts/{accountId}/nickname", new { nickname = (string?)null }));
    using var after = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
    after.RootElement.GetProperty("nickname").ValueKind.Should().Be(JsonValueKind.Null);
  }

  [Fact]
  public async Task ImportProgress_IsAnsweredForTheOwnersConnection_And404ForSomeoneElses()
  {
    var (userId, connectionId, _) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf6@home.test", BankProvider.SimpleFin);
    var (otherId, _, _) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf7@home.test", BankProvider.SimpleFin);

    var mine = await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, $"/api/bank/connections/{connectionId}/import-progress"));
    mine.StatusCode.Should().Be(HttpStatusCode.OK);
    using (var json = JsonDocument.Parse(await mine.Content.ReadAsStringAsync()))
      json.RootElement.GetProperty("connectionId").GetInt32().Should().Be(connectionId);

    var theirs = await Client().SendAsync(BankAccountsTestData.As(otherId, HttpMethod.Get, $"/api/bank/connections/{connectionId}/import-progress"));
    theirs.StatusCode.Should().Be(HttpStatusCode.NotFound);
  }

  [Fact]
  public async Task Providers_OfferSimpleFin_ButNotPlaidOrAnUnpairedCloudFeed()
  {
    var (userId, _, _) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf8@home.test", BankProvider.SimpleFin);

    var response = await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, "/api/bank/providers"));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    var providers = await response.Content.ReadFromJsonAsync<string[]>();
    providers.Should().Equal("SimpleFin");
  }

  [Fact]
  public async Task ThePlaidRoutesStayHostedOnly()
  {
    var (userId, _, _) = await BankAccountsTestData.SeedAsync(_factory.Services, "sf9@home.test", BankProvider.SimpleFin);

    (await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, "/api/plaid/accounts"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    (await Client().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, "/api/plaid/connections"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
  }
}

/// <summary>Hosted: the new routes give exactly the answers the Plaid routes always gave.</summary>
public class BankAccountsHostedIntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
  private readonly TestWebApplicationFactory<Program> _factory;

  public BankAccountsHostedIntegrationTests(TestWebApplicationFactory<Program> factory) => _factory = factory;

  [Fact]
  public async Task Connections_AndAccounts_MatchThePlaidRoutesByteForByte()
  {
    var (userId, _, _) = await BankAccountsTestData.SeedAsync(_factory.Services, "h1@home.test", BankProvider.Plaid);
    var client = _factory.CreateClient();

    foreach (var (oldPath, newPath) in new[] { ("/api/plaid/connections", "/api/bank/connections"), ("/api/plaid/accounts", "/api/bank/accounts") })
    {
      var old = await client.SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, oldPath));
      var now = await client.SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, newPath));

      old.StatusCode.Should().Be(HttpStatusCode.OK, oldPath);
      now.StatusCode.Should().Be(HttpStatusCode.OK, newPath);
      (await now.Content.ReadAsStringAsync()).Should().Be(await old.Content.ReadAsStringAsync(), newPath);
    }
  }

  [Fact]
  public async Task Providers_OfferPlaid()
  {
    var (userId, _, _) = await BankAccountsTestData.SeedAsync(_factory.Services, "h2@home.test", BankProvider.Plaid);

    var response = await _factory.CreateClient().SendAsync(BankAccountsTestData.As(userId, HttpMethod.Get, "/api/bank/providers"));

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    (await response.Content.ReadFromJsonAsync<string[]>()).Should().Contain("Plaid");
  }
}
