using System.Net;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services.SimpleFin;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.SimpleFin;

public class SimpleFinAggregatorTests
{
    // ── start ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_TellsTheClientToAskForAPastedToken_AndWhereToMakeOne()
    {
        using var h = new SimpleFinHarness();

        var start = await h.Aggregator.StartConnectionAsync(SimpleFinHarness.UserId, "web");

        start.Provider.Should().Be(BankProvider.SimpleFin);
        start.Kind.Should().Be(AggregatorConnectKind.SetupTokenPrompt);
        start.Value.Should().Be("https://bridge.simplefin.org/simplefin/create");
        h.Bridge.Requests.Should().BeEmpty();
    }

    // ── connect ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Connect_ClaimsTheToken_StoresTheAccessUrlEncrypted_AndImportsAccountsAndTransactions()
    {
        using var h = new SimpleFinHarness();

        var result = await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        result.Success.Should().BeTrue();
        result.Message.Should().BeNull();

        var connection = await h.Db.PlaidConnections.AsNoTracking().SingleAsync();
        connection.Provider.Should().Be(BankProvider.SimpleFin);
        connection.AccessToken.Should().Be("enc::" + SimpleFinFixtures.AccessUrl);
        connection.AccessToken.Should().NotBe(SimpleFinFixtures.AccessUrl);
        connection.ItemId.Should().StartWith("sfin:").And.NotContain("demo-pass");
        connection.InstitutionName.Should().Be("My Bank");
        connection.Status.Should().Be(PlaidItemStatus.Active);
        connection.ImportStatus.Should().Be(ImportStatus.Completed);
        connection.LastTransactionSyncAt.Should().NotBeNull();
        connection.LastSyncTransactionCount.Should().Be(2);
        result.ExternalConnectionId.Should().Be(connection.ItemId);

        var account = await h.Db.Accounts.AsNoTracking().SingleAsync();
        account.PlaidAccountId.Should().Be($"sfin:{connection.Id}:ACT-checking-1");
        account.PlaidConnectionId.Should().Be(connection.Id);
        account.Type.Should().Be(AccountType.Checking);
        account.CurrentBalance.Should().Be(1204.55m);
        account.AvailableBalance.Should().Be(1100.00m);
        account.Name.Should().Be("Everyday Checking");

        var transactions = await h.Db.Transactions.AsNoTracking().OrderBy(t => t.PlaidTransactionId).ToListAsync();
        transactions.Select(t => t.PlaidTransactionId).Should().Equal($"sfin:{connection.Id}:ACT-checking-1:TRN-1", $"sfin:{connection.Id}:ACT-checking-1:TRN-2");
        transactions[0].Amount.Should().Be(-45.10m);
        transactions[0].Type.Should().Be(TransactionType.Debit);
        transactions[1].Amount.Should().Be(2500.00m);
        transactions[1].Type.Should().Be(TransactionType.Credit);
        transactions.Should().OnlyContain(t => t.NeedsReview && t.CategorySource == "SimpleFin");
    }

    [Fact]
    public async Task Connect_AsksForTheConfiguredNumberOfDays_InOneRequestPerNinetyDays()
    {
        using var h = new SimpleFinHarness(o => o.InitialImportDays = 200);
        h.OnAccounts = _ => SimpleFinHarness.Json(SimpleFinFixtures.Empty);

        await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        h.Bridge.Requests.Count(r => r.Method == HttpMethod.Get).Should().Be(3);
    }

    [Fact]
    public async Task Connect_ThePendingChargeIsSkipped_SoItNeverShowsTwice()
    {
        using var h = new SimpleFinHarness();

        await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        (await h.Db.Transactions.AnyAsync(t => t.PlaidTransactionId.EndsWith(":TRN-3"))).Should().BeFalse();
    }

    [Fact]
    public async Task Connect_ARefusedToken_IsAFriendlyFailure_AndSavesNothing()
    {
        using var h = new SimpleFinHarness();
        h.OnClaim = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var result = await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("make a new one");
        (await h.Db.PlaidConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Connect_ATokenForAnotherHost_IsRefusedWithoutAnyRequest()
    {
        using var h = new SimpleFinHarness();

        var result = await h.Aggregator.CompleteConnectionAsync(
            SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken("http://169.254.169.254/latest/meta-data"));

        result.Success.Should().BeFalse();
        h.Bridge.Requests.Should().BeEmpty();
        (await h.Db.PlaidConnections.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Connect_IfTheFirstSyncFails_TheConnectionIsStillSaved_BecauseTheTokenIsSpent()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("tried again later");
        var connection = await h.Db.PlaidConnections.AsNoTracking().SingleAsync();
        connection.AccessToken.Should().Be("enc::" + SimpleFinFixtures.AccessUrl);
        connection.LastSyncError.Should().NotBeNullOrEmpty();
        connection.Status.Should().Be(PlaidItemStatus.Active);
    }

    [Fact]
    public async Task Connect_IfTheFirstSyncFails_TheNextLoopSyncBackfillsTheWholeInitialWindow()
    {
        using var h = new SimpleFinHarness(o => o.InitialImportDays = 90);
        h.OnAccounts = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());
        var connection = await h.ReloadAsync((await h.Db.PlaidConnections.SingleAsync()).Id);
        connection.LastTransactionSyncAt.Should().BeNull();
        connection.ImportStatus.Should().Be(ImportStatus.Failed);
        connection.LastSyncAttemptAt.Should().NotBeNull();

        h.OnAccounts = _ => SimpleFinHarness.Json(SimpleFinFixtures.Empty);
        h.Bridge.Requests.Clear();
        // The loop asks for only a week; with no good sync yet the aggregator must widen it.
        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        h.Bridge.Requests.Count(r => r.Method == HttpMethod.Get).Should().Be(1); // 90 days fit one slice
        var query = h.Bridge.Requests.Single(r => r.Method == HttpMethod.Get).Uri.Query;
        var start = long.Parse(System.Text.RegularExpressions.Regex.Match(query, @"start-date=(\d+)").Groups[1].Value);
        DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime.Should().BeBefore(DateTime.UtcNow.AddDays(-60));
    }

    [Fact]
    public async Task Connect_APasteWhileTheConnectionIsStillActive_AddsANewConnection_NotOverwritingTheFlaggedOne()
    {
        using var h = new SimpleFinHarness();
        await h.SeedConnectionAsync(c =>
        {
            c.AccessToken = "enc::https://old:old@bridge.simplefin.org/simplefin";
            c.ItemId = "sfin:old";
            c.Status = PlaidItemStatus.Active;
            c.RequiresUserAction = true; // a bank-level notice, fixed at the bridge
        });

        await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        var all = await h.Db.PlaidConnections.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        all.Should().HaveCount(2);
        all[0].AccessToken.Should().Be("enc::https://old:old@bridge.simplefin.org/simplefin");
    }

    [Fact]
    public async Task Connect_APasteForABrokenConnection_RepairsThatConnection_InsteadOfAddingASecondOne()
    {
        using var h = new SimpleFinHarness();
        var broken = await h.SeedConnectionAsync(c =>
        {
            c.AccessToken = "enc::https://old:old@bridge.simplefin.org/simplefin";
            c.ItemId = "sfin:old";
            c.Status = PlaidItemStatus.LoginRequired;
            c.RequiresUserAction = true;
            c.LastError = "no longer accepted";
            c.ConsecutiveFailures = 4;
            c.AlertSentAt = DateTime.UtcNow.AddDays(-1);
        });

        var result = await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        result.Success.Should().BeTrue();
        var connection = await h.Db.PlaidConnections.AsNoTracking().SingleAsync();
        connection.Id.Should().Be(broken.Id);
        connection.AccessToken.Should().Be("enc::" + SimpleFinFixtures.AccessUrl);
        connection.Status.Should().Be(PlaidItemStatus.Active);
        connection.RequiresUserAction.Should().BeFalse();
        connection.LastError.Should().BeNull();
        connection.ConsecutiveFailures.Should().Be(0);
        connection.AlertSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Connect_APasteWhileAHealthyConnectionExists_AddsASecondOne()
    {
        using var h = new SimpleFinHarness();
        await h.SeedConnectionAsync(c => c.ItemId = "sfin:first");

        await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        (await h.Db.PlaidConnections.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Connect_NamesTheConnectionAfterItsBanks()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => SimpleFinHarness.Json(SimpleFinFixtures.ThreeAccounts);

        await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        (await h.Db.PlaidConnections.AsNoTracking().SingleAsync()).InstitutionName.Should().Be("SimpleFIN (2 banks)");
    }

    // ── accounts ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_GuessesTypes_StoresCardDebtAsPositive_AndDropsTheRetirementAccount()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => SimpleFinHarness.Json(SimpleFinFixtures.ThreeAccounts);
        var connection = await h.SeedConnectionAsync();

        var count = await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var accounts = await h.Db.Accounts.AsNoTracking().OrderBy(a => a.PlaidAccountId).ToListAsync();
        accounts.Select(a => a.PlaidAccountId).Should().Equal($"sfin:{connection.Id}:ACT-card-1", $"sfin:{connection.Id}:ACT-checking-1");

        var card = accounts[0];
        card.Type.Should().Be(AccountType.CreditCard);
        card.CurrentBalance.Should().Be(310.20m); // owed, shown the way Plaid shows it
        card.AvailableBalance.Should().Be(4689.80m);

        count.Should().Be(2); // the retirement account's transaction is not imported
        (await h.Db.Transactions.AnyAsync(t => t.PlaidTransactionId.EndsWith(":TRN-77"))).Should().BeFalse();
    }

    [Fact]
    public async Task Sync_AnAccountThatWasImportedBefore_AndNowReadsAsOutOfScope_IsRetired()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => SimpleFinHarness.Json("""{"accounts":[{"id":"ACT-x","name":"Rollover IRA","balance":"5","transactions":[]}]}""");
        var connection = await h.SeedConnectionAsync();
        h.Db.Accounts.Add(new Account
        {
            UserId = SimpleFinHarness.UserId, PlaidConnectionId = connection.Id, PlaidAccountId = $"sfin:{connection.Id}:ACT-x",
            Name = "Rollover IRA", Type = AccountType.Checking, IsActive = true,
        });
        await h.Db.SaveChangesAsync();

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        (await h.Db.Accounts.AsNoTracking().SingleAsync()).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_AnAccountMissingFromTheBridgeList_IsDeactivated_AndComesBackWhenItReappears()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync();
        h.Db.Accounts.Add(new Account
        {
            UserId = SimpleFinHarness.UserId, PlaidConnectionId = connection.Id, PlaidAccountId = $"sfin:{connection.Id}:ACT-gone",
            Name = "Old Savings", Type = AccountType.Savings, IsActive = true,
        });
        await h.Db.SaveChangesAsync();

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var gone = await h.Db.Accounts.AsNoTracking().SingleAsync(a => a.PlaidAccountId.EndsWith("ACT-gone"));
        gone.IsActive.Should().BeFalse();

        h.OnAccounts = _ => SimpleFinHarness.Json("""{"accounts":[{"id":"ACT-gone","name":"Old Savings","balance":"9","transactions":[]}]}""");
        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        (await h.Db.Accounts.AsNoTracking().SingleAsync(a => a.PlaidAccountId.EndsWith("ACT-gone"))).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Sync_UpdatesBalances_AndKeepsTheNickname()
    {
        using var h = new SimpleFinHarness();
        var current = await h.SeedConnectionAsync(c => c.ItemId = "sfin:current");
        h.Db.Accounts.Add(new Account
        {
            UserId = SimpleFinHarness.UserId, PlaidConnectionId = current.Id, PlaidAccountId = $"sfin:{current.Id}:ACT-checking-1",
            Name = "stale", Nickname = "Joint", Type = AccountType.Checking, CurrentBalance = 1m, IsActive = true,
        });
        await h.Db.SaveChangesAsync();

        await h.Aggregator.SyncConnectionTransactionsAsync(current, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var account = await h.Db.Accounts.AsNoTracking().SingleAsync();
        account.CurrentBalance.Should().Be(1204.55m);
        account.Name.Should().Be("Everyday Checking");
        account.Nickname.Should().Be("Joint");
        account.PlaidConnectionId.Should().Be(current.Id);
    }

    [Fact]
    public async Task Sync_TwoConnectionsThatReuseTheSameBridgeIds_KeepSeparateAccountsAndTransactions()
    {
        using var h = new SimpleFinHarness();
        var first = await h.SeedConnectionAsync(c => c.ItemId = "sfin:first");
        var second = await h.SeedConnectionAsync(c => c.ItemId = "sfin:second");

        await h.Aggregator.SyncConnectionTransactionsAsync(first, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        await h.Aggregator.SyncConnectionTransactionsAsync(second, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var accounts = await h.Db.Accounts.AsNoTracking().ToListAsync();
        accounts.Should().HaveCount(2);
        accounts.Select(a => a.PlaidConnectionId).Should().BeEquivalentTo(new int?[] { first.Id, second.Id });
        (await h.Db.Transactions.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task Sync_ALongBridgeAccountName_IsCutToTheColumnWidth_WithoutSplittingAnEmoji()
    {
        using var h = new SimpleFinHarness();
        var name = new string('a', 99) + "😀tail";
        h.OnAccounts = _ => SimpleFinHarness.Json("{\"accounts\":[{\"id\":\"ACT-long\",\"name\":\"" + name + "\",\"org\":{\"name\":\"Big Bank\"},\"balance\":\"5\",\"transactions\":[]}]}");
        var connection = await h.SeedConnectionAsync();

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var account = await h.Db.Accounts.AsNoTracking().SingleAsync();
        account.Name.Length.Should().BeLessThanOrEqualTo(100);
        account.OfficialName!.Length.Should().BeLessThanOrEqualTo(100);
        char.IsHighSurrogate(account.Name[^1]).Should().BeFalse();
        char.IsHighSurrogate(account.OfficialName[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task Connect_WithTwoBrokenConnections_IsRefusedBeforeTheTokenIsSpent()
    {
        using var h = new SimpleFinHarness();
        await h.SeedConnectionAsync(c => { c.ItemId = "sfin:a"; c.Status = PlaidItemStatus.LoginRequired; c.RequiresUserAction = true; });
        await h.SeedConnectionAsync(c => { c.ItemId = "sfin:b"; c.Status = PlaidItemStatus.Error; c.RequiresUserAction = true; });

        var result = await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());

        result.Success.Should().BeFalse();
        h.Bridge.Requests.Should().BeEmpty();
        (await h.Db.PlaidConnections.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Sync_NeverTouchesAnAccountThatBelongsToAnotherUser()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync();
        h.Db.Users.Add(new User { Id = 2, Username = "other", Email = "o@home.test", PasswordHash = "x" });
        h.Db.Accounts.Add(new Account
        {
            UserId = 2, PlaidAccountId = $"sfin:{connection.Id}:ACT-checking-1", Name = "theirs", Type = AccountType.Checking,
            CurrentBalance = 5m, IsActive = true,
        });
        await h.Db.SaveChangesAsync();

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var theirs = await h.Db.Accounts.AsNoTracking().SingleAsync();
        theirs.Name.Should().Be("theirs");
        theirs.CurrentBalance.Should().Be(5m);
        (await h.Db.Transactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sync_PutsNewAccountsInTheUsersFamily()
    {
        using var h = new SimpleFinHarness(familyId: 9);
        h.Db.Families.Add(new Family { Id = 9, Name = "Home" });
        await h.Db.SaveChangesAsync();
        var connection = await h.SeedConnectionAsync();

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        (await h.Db.Accounts.AsNoTracking().SingleAsync()).FamilyId.Should().Be(9);
    }

    // ── transactions ────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_RunTwiceOverTheSameWindow_CreatesEachTransactionOnce()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync();

        var first = await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        var second = await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        first.Should().Be(2);
        second.Should().Be(0);
        (await h.Db.Transactions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Sync_ATransactionRepeatedAcrossSlices_IsCreatedOnce()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync(c => c.LastTransactionSyncAt = DateTime.UtcNow.AddDays(-1));

        // 120 days is two slices; the bridge sends the same charge in both.
        var count = await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-120), DateTime.UtcNow);

        h.Bridge.Requests.Count(r => r.Method == HttpMethod.Get).Should().Be(2);
        count.Should().Be(2);
        (await h.Db.Transactions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Sync_WidensTheWindowByFourDays_ForAFiveDayOverlapWithTheLoopsOwnOneDay()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => SimpleFinHarness.Json(SimpleFinFixtures.Empty);
        var connection = await h.SeedConnectionAsync(c => c.LastTransactionSyncAt = DateTime.UtcNow.AddDays(-1));
        var from = new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, from, from.AddDays(2));

        var query = h.Bridge.Requests.Single().Uri.Query;
        query.Should().Contain($"start-date={new DateTimeOffset(from.AddDays(-4)).ToUnixTimeSeconds()}");
    }

    [Fact]
    public async Task Sync_ATransactionWithNoDescription_GetsAPlaceholder()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => SimpleFinHarness.Json(
            """{"accounts":[{"id":"A","name":"Checking","balance":"1","transactions":[{"id":"T","posted":1759900000,"amount":"-2","description":"   "}]}]}""");
        var connection = await h.SeedConnectionAsync();

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        (await h.Db.Transactions.AsNoTracking().SingleAsync()).Description.Should().Be("Transaction");
    }

    // ── failures and alerts ─────────────────────────────────────────────

    [Fact]
    public async Task Sync_ARefusedConnection_ThrowsTheAuthFailure_ThatTheLoopTurnsIntoAReconnectAlert()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var connection = await h.SeedConnectionAsync();

        var act = () => h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        await act.Should().ThrowAsync<SimpleFinAuthException>();
    }

    [Fact]
    public async Task Sync_AConnectionWithNoSavedAccess_AsksForANewToken()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync(c => c.AccessToken = string.Empty);

        var act = () => h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        await act.Should().ThrowAsync<SimpleFinAuthException>();
        h.Bridge.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Sync_AnUnexpectedFailure_ReachesTheLoopWithoutInternalDetails()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => throw new InvalidOperationException("password=hunter2 at Npgsql.Foo");
        var connection = await h.SeedConnectionAsync();

        var act = () => h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var ex = (await act.Should().ThrowAsync<SimpleFinException>()).Which;
        ex.Message.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task Sync_ASignInProblemAtOneBank_FlagsTheConnection_AlertsOnce_AndKeepsTheOtherData()
    {
        using var h = new SimpleFinHarness();
        h.OnAccounts = _ => SimpleFinHarness.Json(SimpleFinFixtures.StringErrors);
        var connection = await h.SeedConnectionAsync();

        var first = await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        first.Should().Be(1);
        var saved = await h.ReloadAsync(connection.Id);
        saved.RequiresUserAction.Should().BeTrue();
        saved.Status.Should().Be(PlaidItemStatus.Active); // still syncing: the fix is at the bridge, not a new token
        saved.LastError.Should().Contain("[link]").And.NotContain("evil.example");
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "ITEM_LOGIN_REQUIRED"), Times.Once);
    }

    [Fact]
    public async Task Sync_OnceTheBridgeIsHappyAgain_TheFlagClears()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync(c =>
        {
            c.RequiresUserAction = true;
            c.LastError = "sign in again";
        });

        await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);

        var saved = await h.ReloadAsync(connection.Id);
        saved.RequiresUserAction.Should().BeFalse();
        saved.LastError.Should().BeNull();
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task HandleSyncFailure_AnAuthFailure_MovesToLoginRequired_AlertsAtOnce_AndHidesTheMarker()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync();

        await h.Aggregator.HandleSyncFailureAsync(connection.Id, new SimpleFinAuthException("No longer accepted.").Message);

        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.LoginRequired);
        saved.RequiresUserAction.Should().BeTrue();
        saved.IsActive.Should().BeTrue();
        saved.LastError.Should().Be("No longer accepted.");
        saved.LastSyncError.Should().Be("No longer accepted.");
        saved.LastSyncAttemptAt.Should().NotBeNull();
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(
            It.Is<PlaidConnection>(c => c.Id == connection.Id), "ITEM_LOGIN_REQUIRED"), Times.Once);
    }

    [Fact]
    public async Task HandleSyncFailure_PassingFailures_RetryQuietly_UntilTheFourthInARow()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync();

        for (var i = 1; i <= 3; i++)
        {
            await h.Aggregator.HandleSyncFailureAsync(connection.Id, "SimpleFIN returned an error (HTTP 500).");
            var saved = await h.ReloadAsync(connection.Id);
            saved.Status.Should().Be(PlaidItemStatus.Active);
            saved.ConsecutiveFailures.Should().Be(i);
        }
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), It.IsAny<string?>()), Times.Never);

        await h.Aggregator.HandleSyncFailureAsync(connection.Id, "SimpleFIN returned an error (HTTP 500).");

        var failed = await h.ReloadAsync(connection.Id);
        failed.Status.Should().Be(PlaidItemStatus.Error);
        failed.RequiresUserAction.Should().BeTrue();
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "SYNC_FAILED"), Times.Once);
    }

    [Fact]
    public async Task HandleSyncFailure_ForAConnectionThatIsGone_DoesNothing()
    {
        using var h = new SimpleFinHarness();

        var act = () => h.Aggregator.HandleSyncFailureAsync(404, "x");

        await act.Should().NotThrowAsync();
    }

    // ── disconnect ──────────────────────────────────────────────────────

    [Fact]
    public async Task Disconnect_ForgetsTheSecret_AndDeactivates()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync(c =>
        {
            c.RequiresUserAction = true;
            c.AlertSentAt = DateTime.UtcNow;
        });

        await h.Aggregator.DisconnectConnectionAsync(SimpleFinHarness.UserId, connection.Id);

        var saved = await h.ReloadAsync(connection.Id);
        saved.AccessToken.Should().BeEmpty();
        saved.IsActive.Should().BeFalse();
        saved.Status.Should().Be(PlaidItemStatus.Disconnected);
        saved.DisconnectDate.Should().NotBeNull();
        saved.RequiresUserAction.Should().BeFalse();
        saved.AlertSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_SomeoneElsesConnection_ThrowsAndChangesNothing()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync();

        var act = () => h.Aggregator.DisconnectConnectionAsync(userId: 99, connection.Id);

        await act.Should().ThrowAsync<ArgumentException>();
        (await h.ReloadAsync(connection.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Disconnect_APlaidConnection_IsNotThisProvidersToRemove()
    {
        using var h = new SimpleFinHarness();
        var plaid = await h.SeedConnectionAsync(c => c.Provider = BankProvider.Plaid);

        var act = () => h.Aggregator.DisconnectConnectionAsync(SimpleFinHarness.UserId, plaid.Id);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RemoveAtProvider_HasNothingToRevoke_AndSaysDone()
    {
        using var h = new SimpleFinHarness();
        var connection = await h.SeedConnectionAsync();

        (await h.Aggregator.TryRemoveConnectionAsync(connection)).Should().BeTrue();
        h.Bridge.Requests.Should().BeEmpty();
    }

    // ── the sync schedule it asks for ───────────────────────────────────

    [Fact]
    public void Schedule_IsSixHoursWithAnHourOfSpread_ByDefault_AndFollowsTheSettings()
    {
        using var defaults = new SimpleFinHarness();
        defaults.Aggregator.MinimumSyncInterval.Should().Be(TimeSpan.FromHours(6));
        defaults.Aggregator.SyncJitter.Should().Be(TimeSpan.FromMinutes(60));

        using var custom = new SimpleFinHarness(o =>
        {
            o.MinSyncIntervalHours = 12;
            o.JitterMinutes = 15;
        });
        custom.Aggregator.MinimumSyncInterval.Should().Be(TimeSpan.FromHours(12));
        custom.Aggregator.SyncJitter.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Schedule_DefaultsKeepOneConnectionFarUnderTheBridgesDailyRequestBudget()
    {
        using var h = new SimpleFinHarness();

        var attemptsPerDay = TimeSpan.FromDays(1) / h.Aggregator.MinimumSyncInterval;
        var requestsPerAttempt = 1; // a normal sync is one slice

        (attemptsPerDay * requestsPerAttempt).Should().BeLessThanOrEqualTo(24);
    }
}
