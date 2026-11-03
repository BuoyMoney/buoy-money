using System.Net;
using System.Text;
using BuoyAI.Infrastructure.Services.SimpleFin;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.SimpleFin;

public class SimpleFinClientTests
{
    // ── setup token ─────────────────────────────────────────────────────

    [Fact]
    public async Task Claim_PostsOnceToTheDecodedUrl_WithAnEmptyBody_AndReturnsTheAccessUrl()
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.AccessUrl + "\n");
        var client = SimpleFinTestFactory.Client(bridge);

        var accessUrl = await client.ClaimAsync(SimpleFinFixtures.SetupToken());

        accessUrl.Should().Be(SimpleFinFixtures.AccessUrl);
        var request = bridge.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.ToString().Should().Be(SimpleFinFixtures.ClaimUrl);
        request.ContentLength.Should().Be(0);
    }

    [Fact]
    public async Task Claim_AcceptsATokenPastedWithWhitespaceAndUrlSafeBase64()
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.AccessUrl);
        var client = SimpleFinTestFactory.Client(bridge);
        var token = SimpleFinFixtures.SetupToken().Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var accessUrl = await client.ClaimAsync($"  {token[..10]}\n{token[10..]}  ");

        accessUrl.Should().Be(SimpleFinFixtures.AccessUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all !!!")]
    [InlineData("aGVsbG8gd29ybGQ=")] // "hello world": valid Base64, not a URL
    public async Task Claim_RejectsAMalformedToken_WithoutCallingTheBridge(string token)
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK);
        var client = SimpleFinTestFactory.Client(bridge);

        var act = () => client.ClaimAsync(token);

        await act.Should().ThrowAsync<SimpleFinException>();
        bridge.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_RefusesAClaimUrlOutsideTheSimplefinClaimPath_SoTheTokenCannotEscapeTheLogFilters()
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK);
        var client = SimpleFinTestFactory.Client(bridge);

        var act = () => client.ClaimAsync(SimpleFinFixtures.SetupToken("https://bridge.simplefin.org/claim/x"));

        await act.Should().ThrowAsync<SimpleFinException>();
        bridge.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://bridge.simplefin.org/simplefin/claim/x")]        // not https
    [InlineData("https://evil.example/simplefin/claim/x")]               // not SimpleFIN
    [InlineData("https://simplefin.org.evil.example/claim/x")]           // look-alike suffix
    [InlineData("https://notsimplefin.org/claim/x")]                     // look-alike prefix
    [InlineData("https://bridge.simplefin.org:8443/claim/x")]            // other port
    [InlineData("https://user:pw@bridge.simplefin.org/claim/x")]         // embedded credentials
    [InlineData("https://127.0.0.1/claim/x")]                            // an address, not a host
    [InlineData("file:///etc/passwd")]
    public async Task Claim_RefusesToCallAnythingButAllowedSimpleFinHosts(string url)
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK);
        var client = SimpleFinTestFactory.Client(bridge);

        var act = () => client.ClaimAsync(SimpleFinFixtures.SetupToken(url));

        await act.Should().ThrowAsync<SimpleFinException>().WithMessage("*not set up to call*");
        bridge.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Claim_AllowsAnotherHostWhenTheOperatorAddsIt()
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK, "https://u:p@bridge.example.net/simplefin");
        var client = SimpleFinTestFactory.Client(bridge, SimpleFinTestFactory.Options(o => o.AllowedHosts = "simplefin.org, example.net"));

        var accessUrl = await client.ClaimAsync(SimpleFinFixtures.SetupToken("https://bridge.example.net/simplefin/claim/x"));

        accessUrl.Should().Be("https://u:p@bridge.example.net/simplefin");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Claim_ARefusedOrUsedToken_SaysSoWithoutEchoingIt(HttpStatusCode status)
    {
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(status));
        var token = SimpleFinFixtures.SetupToken();

        var act = () => client.ClaimAsync(token);

        var ex = (await act.Should().ThrowAsync<SimpleFinException>()).Which;
        ex.Message.Should().Contain("make a new one");
        ex.Message.Should().NotContain(token).And.NotContain("DEMO-TOKEN-1");
    }

    [Fact]
    public async Task Claim_RejectsAnAccessUrlOnAHostWeDoNotAllow()
    {
        var client = SimpleFinTestFactory.Client(
            FakeBridgeHandler.Always(HttpStatusCode.OK, "https://u:p@evil.example/simplefin"));

        var act = () => client.ClaimAsync(SimpleFinFixtures.SetupToken());

        await act.Should().ThrowAsync<SimpleFinException>();
    }

    [Fact]
    public async Task Claim_ABridgeThatIsDownGivesAFriendlyError()
    {
        var bridge = new FakeBridgeHandler(_ => throw new HttpRequestException("connection refused by 10.0.0.5"));
        var client = SimpleFinTestFactory.Client(bridge);

        var act = () => client.ClaimAsync(SimpleFinFixtures.SetupToken());

        var ex = (await act.Should().ThrowAsync<SimpleFinException>()).Which;
        ex.Message.Should().NotContain("10.0.0.5");
    }

    // ── /accounts ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccounts_SendsTheCredentialsInAHeader_NotTheUrl_AndAsksForTheWindow()
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.OneBank);
        var client = SimpleFinTestFactory.Client(bridge);
        var from = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc);

        await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, from, to);

        var request = bridge.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Get);
        request.Uri.ToString().Should().Be(
            $"https://bridge.simplefin.org/simplefin/accounts?start-date={new DateTimeOffset(from).ToUnixTimeSeconds()}&end-date={new DateTimeOffset(to).ToUnixTimeSeconds()}");
        request.Uri.UserInfo.Should().BeEmpty();
        request.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("demo-user:demo-pass")));
    }

    [Fact]
    public async Task GetAccounts_ReadsAccountsBalancesAndTransactions()
    {
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.OneBank));

        var set = await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

        set.AuthProblem.Should().BeFalse();
        set.Messages.Should().BeEmpty();
        var account = set.Accounts.Should().ContainSingle().Subject;
        account.Id.Should().Be("ACT-checking-1");
        account.Name.Should().Be("Everyday Checking");
        account.OrgName.Should().Be("My Bank");
        account.Currency.Should().Be("USD");
        account.Balance.Should().Be(1204.55m);
        account.AvailableBalance.Should().Be(1100.00m);
        account.Transactions.Should().HaveCount(3);

        var grocery = account.Transactions[0];
        grocery.Id.Should().Be("TRN-1");
        grocery.Amount.Should().Be(-45.10m);
        grocery.Posted!.Value.ToUnixTimeSeconds().Should().Be(1759900000);
        grocery.Pending.Should().BeFalse();

        account.Transactions[1].TransactedAt!.Value.ToUnixTimeSeconds().Should().Be(1759940000);

        // posted = 0 and pending = true: the bridge's way to say "not posted yet".
        account.Transactions[2].Pending.Should().BeTrue();
        account.Transactions[2].Posted.Should().BeNull();
    }

    [Fact]
    public async Task GetAccounts_AcceptsAmountsSentAsNumbers()
    {
        const string json = """{"accounts":[{"id":"A","name":"N","balance":12.5,"transactions":[{"id":"T","posted":1759900000,"amount":-3.25,"description":"d"}]}]}""";
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.OK, json));

        var set = await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        set.Accounts[0].Balance.Should().Be(12.5m);
        set.Accounts[0].Transactions[0].Amount.Should().Be(-3.25m);
    }

    [Fact]
    public async Task GetAccounts_SkipsRowsMissingAnIdOrAmount()
    {
        const string json = """{"accounts":[{"name":"no id"},{"id":"A","name":"N","transactions":[{"posted":1759900000,"amount":"1"},{"id":"T","posted":1759900000},{"id":"OK","posted":1759900000,"amount":"1"}]}]}""";
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.OK, json));

        var set = await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        set.Accounts.Should().ContainSingle();
        set.Accounts[0].Transactions.Select(t => t.Id).Should().Equal("OK");
    }

    [Fact]
    public async Task GetAccounts_StringErrors_AreShownCleaned_AndFlagASignInProblem()
    {
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.StringErrors));

        var set = await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        set.AuthProblem.Should().BeTrue();
        set.Messages.Should().ContainSingle().Which.Should().NotContain("evil.example").And.Contain("[link]");
        set.Accounts.Should().ContainSingle(); // data from the rest still comes through
    }

    [Fact]
    public async Task GetAccounts_CodedErrors_ConAuthIsASignInProblem_AndControlCharactersAreRemoved()
    {
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.ErrList));

        var set = await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        set.AuthProblem.Should().BeTrue();
        set.Messages.Should().ContainSingle().Which.Should().Be("Authentication failed. Sign in at the bank again.");
    }

    [Fact]
    public async Task GetAccounts_OtherCodedErrors_AreNotASignInProblem()
    {
        const string json = """{"errlist":[{"code":"gen.rate","msg":"Slow down"}],"accounts":[]}""";
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.OK, json));

        var set = await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        set.AuthProblem.Should().BeFalse();
        set.Messages.Should().Equal("Slow down");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public async Task GetAccounts_ARefusedConnection_IsAnAuthFailure_ThatNeverEchoesTheCredentials(HttpStatusCode status)
    {
        var client = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(status, "go away demo-pass"));

        var act = () => client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        var ex = (await act.Should().ThrowAsync<SimpleFinAuthException>()).Which;
        ex.Message.Should().StartWith(SimpleFinAuthException.Marker);
        ex.Message.Should().NotContain("demo-pass").And.NotContain("demo-user");
    }

    [Fact]
    public async Task GetAccounts_RateLimitedOrBroken_IsAPassingError_NotAnAuthFailure()
    {
        var limited = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.TooManyRequests));
        var broken = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.InternalServerError));
        var garbage = SimpleFinTestFactory.Client(FakeBridgeHandler.Always(HttpStatusCode.OK, "<html>oops</html>"));

        foreach (var client in new[] { limited, broken, garbage })
        {
            var act = () => client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
            var ex = (await act.Should().ThrowAsync<SimpleFinException>()).Which;
            ex.Should().NotBeOfType<SimpleFinAuthException>();
        }
    }

    [Fact]
    public async Task GetAccounts_RefusesAnAccessUrlOnAnotherHost_BeforeSendingCredentials()
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.OneBank);
        var client = SimpleFinTestFactory.Client(bridge);

        var act = () => client.GetAccountsAsync("https://u:p@evil.example/simplefin", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        await act.Should().ThrowAsync<SimpleFinException>();
        bridge.Requests.Should().BeEmpty();
    }

    // ── 90-day slices ───────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1)]
    [InlineData(89, 1)]
    [InlineData(90, 1)]
    [InlineData(91, 2)]
    [InlineData(180, 2)]
    [InlineData(181, 3)]
    [InlineData(365, 5)]
    public void SplitWindow_NeverAsksForMoreThan90DaysAtATime(int days, int expectedSlices)
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(days);

        var slices = SimpleFinClient.SplitWindow(from, to);

        slices.Should().HaveCount(expectedSlices);
        slices.Should().OnlyContain(s => (s.To - s.From).TotalDays <= 90);
    }

    [Fact]
    public void SplitWindow_SlicesTouchWithoutGapsOrOverlap_OldestFirst()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(200);

        var slices = SimpleFinClient.SplitWindow(from, to);

        slices[0].From.Should().Be(from);
        slices[^1].To.Should().Be(to);
        for (var i = 1; i < slices.Count; i++)
        {
            slices[i].From.Should().Be(slices[i - 1].To);
        }
    }

    [Fact]
    public void SplitWindow_AnEmptyOrBackwardsWindowHasNoSlices()
    {
        var now = DateTime.UtcNow;

        SimpleFinClient.SplitWindow(now, now).Should().BeEmpty();
        SimpleFinClient.SplitWindow(now, now.AddDays(-1)).Should().BeEmpty();
    }

    [Fact]
    public async Task ALongWindow_IsFetchedAsOneRequestPerSlice()
    {
        var bridge = FakeBridgeHandler.Always(HttpStatusCode.OK, SimpleFinFixtures.Empty);
        var client = SimpleFinTestFactory.Client(bridge);
        var to = DateTime.UtcNow;

        foreach (var (from, end) in SimpleFinClient.SplitWindow(to.AddDays(-200), to))
        {
            await client.GetAccountsAsync(SimpleFinFixtures.AccessUrl, from, end);
        }

        bridge.Requests.Should().HaveCount(3);
    }
}
