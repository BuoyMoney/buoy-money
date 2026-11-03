using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class UserDataExportServiceTests : IDisposable
{
    private const string Sentinel = "SENTINEL-SECRET-";

    private readonly BuoyAIDbContext _context;

    public UserDataExportServiceTests()
    {
        _context = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    public void Dispose() => _context.Dispose();

    // ---- helpers ----

    private UserDataExportService Service(int pageSize = 1000) => new(_context, pageSize);

    private async Task<string> JsonTextAsync(int userId, int pageSize = 1000)
    {
        var service = Service(pageSize);
        var scope = await service.ResolveScopeAsync(userId);
        scope.Should().NotBeNull();
        using var ms = new MemoryStream();
        await service.WriteJsonAsync(scope!, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task<JsonDocument> JsonAsync(int userId, int pageSize = 1000) =>
        JsonDocument.Parse(await JsonTextAsync(userId, pageSize));

    private async Task<ZipArchive> ZipAsync(int userId, int pageSize = 1000)
    {
        var service = Service(pageSize);
        var scope = await service.ResolveScopeAsync(userId);
        var ms = new MemoryStream();
        await service.WriteCsvZipAsync(scope!, ms);
        ms.Position = 0;
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static byte[] ReadZipBytes(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        entry.Should().NotBeNull($"zip should contain {name}");
        using var s = entry!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadZipEntry(ZipArchive zip, string name) =>
        new UTF8Encoding(false).GetString(ReadZipBytes(zip, name)).TrimStart('﻿');

    private static List<JsonElement> Rows(JsonDocument doc, string key) =>
        doc.RootElement.GetProperty(key).EnumerateArray().ToList();

    private User AddUser(int id, int? familyId = null)
    {
        var user = new User
        {
            Id = id,
            Username = $"user{id}",
            Email = $"user{id}@example.com",
            FirstName = $"First{id}",
            LastName = $"Last{id}",
            PasswordHash = "hash",
            FamilyId = familyId,
        };
        _context.Users.Add(user);
        return user;
    }

    private Account AddAccount(int id, int userId, int? familyId = null, string name = "Checking")
    {
        var account = new Account { Id = id, UserId = userId, FamilyId = familyId, Name = name, PlaidAccountId = $"plaid-acct-{id}" };
        _context.Accounts.Add(account);
        return account;
    }

    private Transaction AddTx(int id, int accountId, string description = "Coffee", decimal amount = 4.5m) =>
        _context.Transactions.Add(new Transaction
        {
            Id = id,
            AccountId = accountId,
            PlaidTransactionId = $"plaid-tx-{id}",
            Description = description,
            MerchantName = "Merchant",
            Amount = amount,
            Date = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        }).Entity;

    private void AddFamily(int familyId, params (int userId, FamilyMemberRole role)[] members)
    {
        _context.Families.Add(new Family { Id = familyId, Name = "Test Family", CreatedByUserId = members[0].userId });
        var i = 1;
        foreach (var (userId, role) in members)
        {
            _context.FamilyMembers.Add(new FamilyMember
            {
                Id = familyId * 100 + i++, FamilyId = familyId, UserId = userId, Role = role, IsActive = true,
            });
        }
    }

    // ---- scoping ----

    [Fact]
    public async Task ResolveScope_UnknownUser_ReturnsNull()
    {
        (await Service().ResolveScopeAsync(999)).Should().BeNull();
    }

    [Fact]
    public async Task Export_OwnRowsOnly_NotAnotherUsersRows()
    {
        AddUser(1);
        AddUser(2);
        AddAccount(10, 1);
        AddAccount(20, 2);
        AddTx(100, 10, "mine");
        AddTx(200, 20, "theirs");
        _context.Budgets.Add(new Budget { Id = 1, UserId = 1, Name = "Mine" });
        _context.Budgets.Add(new Budget { Id = 2, UserId = 2, Name = "Theirs" });
        _context.SavingsGoals.Add(new SavingsGoal { Id = 1, UserId = 2, Name = "Their goal" });
        _context.Notifications.Add(new Notification { Id = 1, UserId = 2, Type = "t", Title = "x", Message = "y" });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        doc.RootElement.GetProperty("user").GetProperty("id").GetInt32().Should().Be(1);
        Rows(doc, "accounts").Select(a => a.GetProperty("id").GetInt32()).Should().Equal(10);
        Rows(doc, "transactions").Select(t => t.GetProperty("description").GetString()).Should().Equal("mine");
        Rows(doc, "budgets").Select(b => b.GetProperty("name").GetString()).Should().Equal("Mine");
        Rows(doc, "savingsGoals").Should().BeEmpty();
        Rows(doc, "notifications").Should().BeEmpty();
        Rows(doc, "accounts").Should().OnlyContain(a => !a.GetProperty("shared").GetBoolean());
        doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Export_FamilySharedRows_MarkedShared_WithOwnerIdOnly()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.Member));
        AddAccount(10, 1, familyId: 5, name: "Mine");
        AddAccount(20, 2, familyId: 5, name: "Partner shared");
        AddTx(100, 10);
        AddTx(200, 20);
        _context.Budgets.Add(new Budget { Id = 2, UserId = 2, FamilyId = 5, Name = "Family budget" });
        await _context.SaveChangesAsync();

        var text = await JsonTextAsync(1);
        using var doc = JsonDocument.Parse(text);

        var accounts = Rows(doc, "accounts");
        accounts.Should().HaveCount(2);
        accounts.Single(a => a.GetProperty("id").GetInt32() == 10).GetProperty("shared").GetBoolean().Should().BeFalse();
        var shared = accounts.Single(a => a.GetProperty("id").GetInt32() == 20);
        shared.GetProperty("shared").GetBoolean().Should().BeTrue();
        shared.GetProperty("ownerUserId").GetInt32().Should().Be(2);
        var txs = Rows(doc, "transactions");
        txs.Single(t => t.GetProperty("id").GetInt32() == 200).GetProperty("shared").GetBoolean().Should().BeTrue();
        txs.Single(t => t.GetProperty("id").GetInt32() == 100).GetProperty("shared").GetBoolean().Should().BeFalse();
        Rows(doc, "budgets").Single().GetProperty("shared").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("family").GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("userId").GetInt32()).Should().BeEquivalentTo(new[] { 1, 2 });
        text.Should().NotContain("user2@example.com").And.NotContain("First2").And.NotContain("Last2");
    }

    [Fact]
    public async Task Export_RowsOfAnotherFamilyOrNullFamily_Excluded()
    {
        AddUser(1, familyId: 5);
        AddUser(3, familyId: 6);
        AddFamily(5, (1, FamilyMemberRole.Owner));
        AddFamily(6, (3, FamilyMemberRole.Owner));
        AddAccount(30, 3, familyId: 6, name: "Other family");
        AddAccount(31, 3, familyId: null, name: "No family");
        AddTx(300, 30);
        AddTx(301, 31);
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "accounts").Should().BeEmpty();
        Rows(doc, "transactions").Should().BeEmpty();
    }

    [Fact]
    public async Task Export_FamilyIdButNoActiveMembership_GetsNoFamilyRows()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner));
        _context.FamilyMembers.Add(new FamilyMember { Id = 999, FamilyId = 5, UserId = 1, IsActive = false });
        AddAccount(20, 2, familyId: 5);
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "accounts").Should().BeEmpty();
        doc.RootElement.GetProperty("family").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Export_ReadOnlyMember_DoesNotGetHiddenTransactions_NorSplitChildrenOfHiddenOnes()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.ReadOnly));
        AddAccount(20, 2, familyId: 5);
        AddTx(200, 20, "visible");
        AddTx(201, 20, "hidden parent").HiddenFromReadOnly = true;
        var child = AddTx(202, 20, "child of hidden");
        child.ParentTransactionId = 201;
        child.HiddenFromReadOnly = false;
        var okChild = AddTx(203, 20, "child of visible");
        okChild.ParentTransactionId = 200;
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "transactions").Select(t => t.GetProperty("description").GetString())
            .Should().BeEquivalentTo("visible", "child of visible");
    }

    [Fact]
    public async Task Export_OwnerOfHiddenTransaction_StillGetsIt()
    {
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner));
        AddAccount(20, 2, familyId: 5);
        AddTx(201, 20, "gift").HiddenFromReadOnly = true;
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(2);

        Rows(doc, "transactions").Should().ContainSingle();
    }

    [Fact]
    public async Task Export_ConversationsNestMessages_AndAreOwnOnly()
    {
        AddUser(1);
        AddUser(2);
        _context.AiConversations.Add(new AiConversation { Id = 1, UserId = 1, SessionId = Guid.NewGuid(), Title = "Mine" });
        _context.AiConversations.Add(new AiConversation { Id = 2, UserId = 2, SessionId = Guid.NewGuid(), Title = "Theirs" });
        _context.AiConversationMessages.Add(new AiConversationMessage { Id = 1, ConversationId = 1, Role = "user", Content = "hi" });
        _context.AiConversationMessages.Add(new AiConversationMessage { Id = 2, ConversationId = 2, Role = "user", Content = "secret chat" });
        await _context.SaveChangesAsync();

        var text = await JsonTextAsync(1);
        using var doc = JsonDocument.Parse(text);

        var conversation = Rows(doc, "aiConversations").Single();
        conversation.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("content").GetString()).Should().Equal("hi");
        text.Should().NotContain("secret chat");
    }

    [Fact]
    public async Task Export_SystemCategoryGroups_AreIncluded_SoGroupIdsResolve()
    {
        AddUser(1);
        _context.CategoryGroups.Add(new CategoryGroup { Id = 1, Name = "System group", UserId = null, FamilyId = null });
        _context.CategoryGroups.Add(new CategoryGroup { Id = 2, Name = "Mine", UserId = 1 });
        _context.CategoryGroups.Add(new CategoryGroup { Id = 3, Name = "Theirs", UserId = 9 });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        var groups = Rows(doc, "categoryGroups");
        groups.Select(g => g.GetProperty("name").GetString()).Should().BeEquivalentTo("System group", "Mine");
        groups.Single(g => g.GetProperty("name").GetString() == "System group")
            .GetProperty("shared").GetBoolean().Should().BeFalse();
    }

    // ---- tables that used to be missing ----

    [Fact]
    public async Task Export_IncludesTheFormerlyMissingTables_AndKeepsThemPrivate()
    {
        AddUser(1);
        AddUser(2);
        var sub = _context.Subscriptions.Add(new Subscription { Id = 1, UserId = 1, Name = "Netflix", NextDueDate = new DateTime(2026, 4, 2) }).Entity;
        _context.SubscriptionTransactions.Add(new SubscriptionTransaction { Id = 1, SubscriptionId = sub.Id, TransactionId = 5 });
        _context.UserCategories.Add(new UserCategory { Id = 1, UserId = 1, Name = "Mine" });
        _context.UserCategories.Add(new UserCategory { Id = 2, UserId = 2, Name = "Theirs" });
        _context.SuggestedSubscriptions.Add(new SuggestedSubscription { Id = 1, UserId = 1, DisplayMerchantName = "Spotify" });
        _context.DismissedSubscriptionPatterns.Add(new DismissedSubscriptionPattern { Id = 1, UserId = 1, MerchantName = "Gym" });
        _context.UserOnboardingQuizzes.Add(new UserOnboardingQuiz { Id = 1, UserId = 1, PrimaryFinancialGoal = "save_more" });
        _context.UserVisualizations.Add(new UserVisualization { Id = 1, UserId = 1, Title = "Chart" });
        var budget = _context.Budgets.Add(new Budget { Id = 1, UserId = 1, Name = "B" }).Entity;
        _context.BudgetShares.Add(new BudgetShare { Id = 1, BudgetId = budget.Id, SharedWithUserId = 2, CanEdit = true });
        var snap = _context.BudgetPeriodSnapshots.Add(new BudgetPeriodSnapshot { Id = 1, BudgetId = budget.Id }).Entity;
        _context.BudgetPeriodCategorySnapshots.Add(new BudgetPeriodCategorySnapshot { Id = 1, BudgetPeriodSnapshotId = snap.Id, CategoryId = 3, CategoryName = "Food" });
        _context.AiToolExecutions.Add(new AiToolExecution { Id = 1, UserId = 1, MessageId = 1, ToolName = "get_budgets" });
        _context.AiActionFeedback.Add(new AiActionFeedback { Id = 1, UserId = 1, ToolExecutionId = 1, ThumbsUp = true });
        _context.Referrals.Add(new Referral { Id = 1, ReferrerUserId = 1, RefereeUserId = 2, ReferralCode = "ABC" });
        _context.Referrals.Add(new Referral { Id = 2, ReferrerUserId = 2, RefereeUserId = 1, ReferralCode = "XYZ" });
        _context.PlaidConnections.Add(new PlaidConnection { Id = 1, UserId = 1, InstitutionName = "Bank One", AccessToken = "tok", ItemId = "item" });
        _context.PlaidConnections.Add(new PlaidConnection { Id = 2, UserId = 2, InstitutionName = "Bank Two", AccessToken = "tok2", ItemId = "item2" });
        var plan = _context.UserSubscriptions.Add(new UserSubscription { Id = 1, UserId = 1, Amount = 10m }).Entity;
        _context.SubscriptionPayments.Add(new SubscriptionPayment { Id = 1, UserSubscriptionId = plan.Id, Amount = 10m });
        var other = _context.UserSubscriptions.Add(new UserSubscription { Id = 2, UserId = 2, Amount = 99m }).Entity;
        _context.SubscriptionPayments.Add(new SubscriptionPayment { Id = 2, UserSubscriptionId = other.Id, Amount = 99m });
        await _context.SaveChangesAsync();

        var text = await JsonTextAsync(1);
        using var doc = JsonDocument.Parse(text);

        Rows(doc, "subscriptionTransactions").Should().ContainSingle();
        Rows(doc, "userCategories").Select(c => c.GetProperty("name").GetString()).Should().Equal("Mine");
        Rows(doc, "suggestedSubscriptions").Should().ContainSingle();
        Rows(doc, "dismissedSubscriptionPatterns").Should().ContainSingle();
        Rows(doc, "onboardingQuiz").Should().ContainSingle();
        Rows(doc, "visualizations").Should().ContainSingle();
        Rows(doc, "budgetShares").Should().ContainSingle();
        Rows(doc, "budgetPeriodSnapshots").Should().ContainSingle();
        Rows(doc, "budgetPeriodCategorySnapshots").Should().ContainSingle();
        Rows(doc, "aiToolExecutions").Should().ContainSingle();
        Rows(doc, "aiActionFeedback").Should().ContainSingle();
        var referrals = Rows(doc, "referrals");
        referrals.Should().ContainSingle();
        referrals[0].TryGetProperty("refereeUserId", out _).Should().BeFalse("the referred person is not identified");
        Rows(doc, "plaidConnections").Select(c => c.GetProperty("institutionName").GetString()).Should().Equal("Bank One");
        Rows(doc, "userSubscriptions").Should().ContainSingle();
        Rows(doc, "subscriptionPayments").Should().ContainSingle().Which.GetProperty("amount").GetDecimal().Should().Be(10m);
    }

    [Fact]
    public async Task Export_PlaidConnections_AreForTheConnectionOwnerOnly_NotFamilyMembers()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.Member));
        _context.PlaidConnections.Add(new PlaidConnection { Id = 1, UserId = 2, FamilyId = 5, InstitutionName = "Shared bank" });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "plaidConnections").Should().BeEmpty();
    }

    [Fact]
    public async Task Export_MfaIsOnlyEnabledFlagAndMethod()
    {
        var user = AddUser(1);
        user.HasMfaEnabled = true;
        user.MfaType = MfaType.Totp;
        user.TotpSecret = Sentinel + "Totp";
        await _context.SaveChangesAsync();

        var text = await JsonTextAsync(1);
        using var doc = JsonDocument.Parse(text);

        var mfa = doc.RootElement.GetProperty("preferences").GetProperty("mfa");
        mfa.GetProperty("enabled").GetBoolean().Should().BeTrue();
        mfa.GetProperty("method").GetString().Should().Be("Totp");
        text.Should().NotContain(Sentinel);
    }

    // ---- coverage contract ----

    [Fact]
    public void EveryUserOwnedTable_IsExportedOrOnTheWrittenExclusionList()
    {
        var exported = UserDataExportService.ExportedEntityTypes;
        var excluded = UserDataExportService.ExcludedEntityReasons;
        var all = exported.Concat(excluded.Keys).ToHashSet();

        // Relevant = a column that names a user or family (UserId, ReferrerUserId, SharedWithUserId,
        // CreatedByUserId, OwnerUserId, FamilyId, ...) or a foreign key into User, Family or any
        // exported OR excluded table (so a child of an excluded parent cannot slip through).
        bool IsRelevant(IEntityType t)
        {
            if (t.ClrType == typeof(User)) return true;
            if (t.GetProperties().Any(p => p.Name.EndsWith("UserId") || p.Name == "FamilyId")) return true;
            return t.GetForeignKeys().Any(fk =>
                fk.PrincipalEntityType.ClrType == typeof(User)
                || fk.PrincipalEntityType.ClrType == typeof(Family)
                || all.Contains(fk.PrincipalEntityType.ClrType));
        }

        var modelTypes = _context.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.ClrType != typeof(Dictionary<string, object>))
            .ToList();

        var missing = modelTypes.Where(IsRelevant).Select(t => t.ClrType)
            .Where(t => !all.Contains(t))
            .Select(t => t.Name).OrderBy(n => n).ToList();
        missing.Should().BeEmpty(
            "every table that holds user data must be exported or listed in ExcludedEntityReasons with a reason");

        // No stale keys: every listed type must still be a real entity.
        var modelClr = modelTypes.Select(t => t.ClrType).ToHashSet();
        all.Where(t => !modelClr.Contains(t)).Select(t => t.Name).Should().BeEmpty("stale entries in the coverage lists");

        excluded.Values.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r));
        exported.Intersect(excluded.Keys).Should().BeEmpty();
    }

    [Fact]
    public async Task ExportedEntityTypes_MatchesWhatTheExportActuallyWrites()
    {
        AddUser(1);
        await _context.SaveChangesAsync();
        var service = Service();
        var scope = await service.ResolveScopeAsync(1);

        var written = service.WrittenEntityTypes(scope!);

        written.Except(UserDataExportService.ExportedEntityTypes).Select(t => t.Name)
            .Should().BeEmpty("a table is written but not declared as exported");
        UserDataExportService.ExportedEntityTypes.Except(written).Select(t => t.Name)
            .Should().BeEmpty("a type is declared exported but nothing writes it");
    }

    [Fact]
    public void ExportDtos_NeverUseDateTimeOffset_WhichTheDateConvertersDoNotCover()
    {
        var dtoTypes = typeof(BuoyAI.Core.DTOs.Export.ExportUserDto).Assembly.GetTypes()
            .Where(t => t.Namespace == "BuoyAI.Core.DTOs.Export");

        var offenders = dtoTypes
            .SelectMany(t => t.GetProperties().Select(p => (t, p)))
            .Where(x => x.p.PropertyType == typeof(DateTimeOffset) || x.p.PropertyType == typeof(DateTimeOffset?))
            .Select(x => $"{x.t.Name}.{x.p.Name}")
            .ToList();

        offenders.Should().BeEmpty();
    }

    // ---- read-only members and the tables that follow them ----

    [Fact]
    public async Task Export_ReadOnlyMember_GetsAiToolExecutionsWithoutArgumentsAndResults()
    {
        AddUser(1, familyId: 5);
        AddFamily(5, (1, FamilyMemberRole.ReadOnly));
        _context.AiToolExecutions.Add(new AiToolExecution
        {
            Id = 1, UserId = 1, MessageId = 1, ToolName = "get_budgets",
            Arguments = Sentinel + "args", Result = Sentinel + "family data",
        });
        await _context.SaveChangesAsync();

        var text = await JsonTextAsync(1);

        text.Should().Contain("get_budgets").And.NotContain(Sentinel);
    }

    [Fact]
    public async Task Export_FullMember_KeepsAiToolExecutionArgumentsAndResults()
    {
        AddUser(1, familyId: 5);
        AddFamily(5, (1, FamilyMemberRole.Owner));
        _context.AiToolExecutions.Add(new AiToolExecution
        {
            Id = 1, UserId = 1, MessageId = 1, ToolName = "get_budgets", Arguments = "{\"a\":1}", Result = "ok",
        });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "aiToolExecutions").Single().GetProperty("result").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Export_ReadOnlyMember_DoesNotGetSubscriptionLinksToHiddenTransactions()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.ReadOnly));
        AddAccount(20, 2, familyId: 5);
        AddTx(200, 20, "visible");
        AddTx(201, 20, "gift").HiddenFromReadOnly = true;
        _context.Subscriptions.Add(new Subscription { Id = 1, UserId = 1, FamilyId = 5, Name = "My sub" });
        _context.SubscriptionTransactions.Add(new SubscriptionTransaction { Id = 1, SubscriptionId = 1, TransactionId = 200 });
        _context.SubscriptionTransactions.Add(new SubscriptionTransaction { Id = 2, SubscriptionId = 1, TransactionId = 201 });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "subscriptionTransactions").Select(r => r.GetProperty("transactionId").GetInt32()).Should().Equal(200);
    }

    [Fact]
    public async Task Export_FamilySubscriptionOwnedByAnotherMember_IsNotIncluded()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.BudgetAdmin));
        _context.Subscriptions.Add(new Subscription { Id = 1, UserId = 1, FamilyId = 5, Name = "Mine" });
        _context.Subscriptions.Add(new Subscription { Id = 2, UserId = 2, FamilyId = 5, Name = "Partner sub" });
        await _context.SaveChangesAsync();

        var text = await JsonTextAsync(1);
        using var doc = JsonDocument.Parse(text);

        Rows(doc, "subscriptions").Select(r => r.GetProperty("name").GetString()).Should().Equal("Mine");
        text.Should().NotContain("Partner sub");
    }

    [Fact]
    public async Task Export_AccountAttributionAndTransactionClaimFields_AreIncluded_PersonalCategoryOnlyForClaimer()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.BudgetAdmin));
        AddAccount(20, 2, familyId: 5).AssociatedUserId = 1;
        var mine = AddTx(200, 20, "claimed by me");
        mine.ClaimedByUserId = 1;
        mine.PersonalUserCategoryId = 7;
        mine.NeedsReview = true;
        mine.SemanticTags = "coffee, cafe";
        var theirs = AddTx(201, 20, "claimed by partner");
        theirs.ClaimedByUserId = 2;
        theirs.PersonalUserCategoryId = 8;
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "accounts").Single().GetProperty("associatedUserId").GetInt32().Should().Be(1);
        var txs = Rows(doc, "transactions");
        var a = txs.Single(t => t.GetProperty("id").GetInt32() == 200);
        a.GetProperty("claimedByUserId").GetInt32().Should().Be(1);
        a.GetProperty("personalUserCategoryId").GetInt32().Should().Be(7);
        a.GetProperty("needsReview").GetBoolean().Should().BeTrue();
        a.GetProperty("semanticTags").GetString().Should().Be("coffee, cafe");
        var b = txs.Single(t => t.GetProperty("id").GetInt32() == 201);
        b.GetProperty("claimedByUserId").GetInt32().Should().Be(2);
        b.GetProperty("personalUserCategoryId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Export_PasskeyOnlyAccount_ReportsPasskeyExplicitly()
    {
        var user = AddUser(1);
        user.HasPasskeyRegistered = true;
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        var mfa = doc.RootElement.GetProperty("preferences").GetProperty("mfa");
        mfa.GetProperty("enabled").GetBoolean().Should().BeTrue();
        mfa.GetProperty("method").GetString().Should().Be("None");
        mfa.GetProperty("passkeyRegistered").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Export_FamilyMembersPersonalCategory_ReferencedByFamilyBudget_IsIncluded()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddUser(9);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.BudgetAdmin));
        _context.Categories.Add(new Category { Id = 301, Name = "Partner personal", UserId = 2 });
        _context.Categories.Add(new Category { Id = 302, Name = "Partner unused", UserId = 2 });
        _context.Categories.Add(new Category { Id = 303, Name = "Stranger", UserId = 9 });
        var budget = _context.Budgets.Add(new Budget { Id = 2, UserId = 2, FamilyId = 5, Name = "Family budget" }).Entity;
        _context.BudgetCategories.Add(new BudgetCategory { Id = 1, BudgetId = budget.Id, CategoryId = 301 });
        _context.BudgetCategories.Add(new BudgetCategory { Id = 2, BudgetId = budget.Id, CategoryId = 303 });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "categories").Select(c => c.GetProperty("name").GetString())
            .Should().Contain("Partner personal").And.NotContain("Partner unused").And.NotContain("Stranger");
    }

    [Fact]
    public async Task Scope_PasskeyOnlyAccount_CountsAsMfa()
    {
        var user = AddUser(1);
        user.HasMfaEnabled = false;
        user.HasPasskeyRegistered = true;
        await _context.SaveChangesAsync();

        var scope = await Service().ResolveScopeAsync(1);

        scope!.HasMfa.Should().BeTrue();
        scope.Preferences.Mfa.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Scope_AccountWithNoSecondFactor_IsNotMfa()
    {
        AddUser(1);
        await _context.SaveChangesAsync();

        (await Service().ResolveScopeAsync(1))!.HasMfa.Should().BeFalse();
    }

    // ---- secrets ----

    [Fact]
    public async Task FullyPopulatedUser_NoSecretSentinelAppearsInJsonOrAnyCsv()
    {
        var user = AddUser(1, familyId: 5);
        user.PasswordHash = Sentinel + "PasswordHash";
        user.TotpSecret = Sentinel + "TotpSecret";
        user.RecoveryCodes = Sentinel + "RecoveryCodes";
        user.AppleRefreshToken = Sentinel + "AppleRefreshToken";
        user.StripeCustomerId = Sentinel + "StripeCustomerId";
        user.GoogleId = Sentinel + "GoogleId";
        user.AppleId = Sentinel + "AppleId";
        user.ReferralCode = Sentinel + "ReferralCode";
        user.AvatarImage = Encoding.UTF8.GetBytes(Sentinel + "Avatar");
        user.ProfilePictureUrl = Sentinel + "ProfilePictureUrl";
        AddFamily(5, (1, FamilyMemberRole.Owner));
        AddAccount(10, 1, familyId: 5);
        AddTx(100, 10);
        _context.PlaidConnections.Add(new PlaidConnection
        {
            Id = 1, UserId = 1, AccessToken = Sentinel + "PlaidAccessToken", ItemId = Sentinel + "PlaidItemId",
            InstitutionId = "ins", InstitutionName = "Bank", UpdateModeUrl = Sentinel + "UpdateUrl",
            LastError = Sentinel + "LastError",
        });
        var plan = _context.UserSubscriptions.Add(new UserSubscription
        {
            Id = 1, UserId = 1, StripeCustomerId = Sentinel + "SubCustomer", StripeSubscriptionId = Sentinel + "SubId",
            ExternalId = Sentinel + "ExternalId",
        }).Entity;
        _context.SubscriptionPayments.Add(new SubscriptionPayment
        {
            Id = 1, UserSubscriptionId = plan.Id, StripePaymentIntentId = Sentinel + "Intent", StripeInvoiceId = Sentinel + "Invoice",
        });
        _context.RefreshTokens.Add(new RefreshToken { Id = 1, UserId = 1, TokenHash = Sentinel + "RefreshToken" });
        _context.PasswordResetTokens.Add(new PasswordResetToken { Id = 1, UserId = 1, Token = Sentinel + "ResetToken" });
        _context.TwoFactorCodes.Add(new TwoFactorCode { Id = 1, UserId = 1, Code = Sentinel + "2FACode", Purpose = "login" });
        _context.UserPasskeys.Add(new UserPasskey
        {
            Id = 1, UserId = 1, CredentialId = Sentinel + "PasskeyCred", PublicKey = Sentinel + "PasskeyKey", Name = "key",
        });
        _context.TrustedDevices.Add(new TrustedDevice { Id = 1, UserId = 1, DeviceFingerprint = Sentinel + "Fingerprint" });
        _context.UserDeviceTokens.Add(new UserDeviceToken { Id = 1, UserId = 1, Token = Sentinel + "PushToken", Platform = "android" });
        _context.UserConsents.Add(new UserConsent
        {
            Id = 1, UserId = 1, ConsentType = "ai", ConsentVersion = "1", HasConsented = true,
            IpAddress = Sentinel + "Ip", UserAgent = Sentinel + "UserAgent",
        });
        _context.UserEmailPreferences.Add(new UserEmailPreferences { Id = 1, UserId = 1 });
        _context.UserPushPreferences.Add(new UserPushPreferences { Id = 1, UserId = 1 });
        await _context.SaveChangesAsync();

        var json = await JsonTextAsync(1);
        json.Should().NotContain(Sentinel);
        // The allow-listed user fields are present, so the check above is not vacuous.
        json.Should().Contain("user1@example.com");

        using var zip = await ZipAsync(1);
        foreach (var entry in zip.Entries)
        {
            ReadZipEntry(zip, entry.FullName).Should().NotContain(Sentinel, $"{entry.FullName} must not leak secrets");
        }

        foreach (var forbidden in new[]
        {
            "passwordHash", "totpSecret", "recoveryCodes", "appleRefreshToken", "stripeCustomerId",
            "stripeSubscriptionId", "stripePaymentIntentId", "stripeInvoiceId", "externalId", "accessToken",
            "itemId", "updateModeUrl", "refreshToken", "tokenHash", "resetToken", "credentialId",
            "publicKey", "deviceFingerprint", "pushToken", "ipAddress", "userAgent",
        })
        {
            json.Should().NotContainEquivalentOf($"\"{forbidden}\"");
        }
    }

    // ---- JSON shape ----

    [Fact]
    public async Task Json_HasExpectedTopLevelKeys_ExcludedList_AndDateOnlyFormats()
    {
        AddUser(1);
        AddAccount(10, 1);
        AddTx(100, 10);
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        keys.Should().Contain(new[]
        {
            "exportedAt", "schemaVersion", "user", "accounts", "transactions", "budgets", "budgetCategories",
            "budgetTransfers", "budgetShares", "budgetPeriodSnapshots", "budgetPeriodCategorySnapshots",
            "categories", "categoryGroups", "userCategories", "categorizationRules", "savingsGoals",
            "savingsAllocations", "subscriptions", "subscriptionTransactions", "suggestedSubscriptions",
            "dismissedSubscriptionPatterns", "aiConversations", "aiMemories", "aiToolExecutions",
            "aiActionFeedback", "insights", "visualizations", "notifications", "achievements", "streaks",
            "challenges", "consents", "onboardingQuiz", "plaidConnections", "referrals", "userSubscriptions",
            "userEntitlements", "subscriptionPayments", "preferences", "family", "excluded",
        });
        doc.RootElement.GetProperty("exportedAt").GetString().Should().EndWith("Z");
        doc.RootElement.GetProperty("excluded").EnumerateArray().Should().NotBeEmpty();
        // Posted dates are plain dates, not midnight UTC timestamps; audit columns keep the time.
        var tx = Rows(doc, "transactions").Single();
        tx.GetProperty("date").GetString().Should().Be("2026-03-01");
        tx.GetProperty("createdAt").GetString().Should().Contain("T");
    }

    // ---- CSV zip ----

    [Fact]
    public async Task CsvZip_ContainsOneEntryPerTable_WithHeaderRows_AndBomOnCsvOnly()
    {
        AddUser(1);
        AddAccount(10, 1);
        AddTx(100, 10, amount: -12.5m);
        await _context.SaveChangesAsync();

        using var zip = await ZipAsync(1);

        zip.Entries.Select(e => e.FullName).Should().Contain(new[]
        {
            "user.csv", "accounts.csv", "transactions.csv", "budgets.csv", "budget_categories.csv",
            "budget_transfers.csv", "budget_shares.csv", "budget_period_snapshots.csv",
            "budget_period_category_snapshots.csv", "categories.csv", "category_groups.csv",
            "user_categories.csv", "categorization_rules.csv", "savings_goals.csv", "savings_allocations.csv",
            "subscriptions.csv", "subscription_transactions.csv", "suggested_subscriptions.csv",
            "dismissed_subscription_patterns.csv", "ai_conversations.csv", "ai_messages.csv", "ai_memories.csv",
            "ai_tool_executions.csv", "ai_action_feedback.csv", "insights.csv", "visualizations.csv",
            "notifications.csv", "achievements.csv", "streaks.csv", "challenges.csv", "consents.csv",
            "onboarding_quiz.csv", "plaid_connections.csv", "referrals.csv", "user_subscriptions.csv",
            "user_entitlements.csv", "subscription_payments.csv", "preferences_email.csv", "preferences_push.csv", "preferences_mfa.csv",
            "family.csv", "family_members.csv", "EXCLUDED.md",
        });

        ReadZipBytes(zip, "transactions.csv").Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        ReadZipBytes(zip, "EXCLUDED.md").Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF });
        ReadZipEntry(zip, "EXCLUDED.md").Should().Contain("Passwords").And.NotContain("SENTINEL");

        var lines = ReadZipEntry(zip, "transactions.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("id,accountId,shared,date,");
        // Negative numbers stay numeric (the formula guard only touches text), invariant culture.
        lines[1].Should().Contain(",-12.5,");
        lines[1].Should().Contain("2026-03-01");
        lines[1].Should().NotContain("2026-03-01T");

        // Empty tables still carry a header row.
        ReadZipEntry(zip, "budgets.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("cr\rlf", "\"cr\rlf\"")]
    [InlineData("", "")]
    public void CsvCell_QuotesPerRfc4180(string input, string expected)
    {
        UserDataExportService.CsvCell(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
    [InlineData("+1+1", "'+1+1")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tTabbed", "'\tTabbed")]
    [InlineData("\rReturn", "'\rReturn")]
    [InlineData("\n=HYPERLINK()", "'\n=HYPERLINK()")]
    [InlineData("Normal =text", "Normal =text")]
    [InlineData("", "")]
    public void GuardFormula_PrefixesDangerousLeadingCharacters(string input, string expected)
    {
        UserDataExportService.GuardFormula(input).Should().Be(expected);
    }

    [Fact]
    public async Task CsvZip_EscapesAndGuardsTextCells_ButLeavesNegativeNumbersAlone()
    {
        AddUser(1);
        AddAccount(10, 1);
        AddTx(100, 10, description: "=HYPERLINK(\"http://evil\",\"x\")", amount: -3m);
        AddTx(101, 10, description: "Rent, March");
        AddTx(102, 10, description: "\n+cmd");
        await _context.SaveChangesAsync();

        using var zip = await ZipAsync(1);
        var tx = ReadZipEntry(zip, "transactions.csv");

        // Guard prefix first, then RFC 4180 quoting of the quotes and comma.
        tx.Should().Contain("\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"");
        tx.Should().Contain("\"Rent, March\"");
        tx.Should().Contain("\"'\n+cmd\"");
        tx.Should().Contain(",-3,");
    }

    // ---- paging ----

    [Fact]
    public async Task Paging_SmallPages_ExportEveryTransactionAndMessageExactlyOnce_InIdOrder()
    {
        AddUser(1);
        AddAccount(10, 1);
        for (var i = 1; i <= 7; i++) AddTx(i, 10, $"tx{i}");
        _context.AiConversations.Add(new AiConversation { Id = 1, UserId = 1, SessionId = Guid.NewGuid() });
        for (var i = 1; i <= 5; i++)
            _context.AiConversationMessages.Add(new AiConversationMessage { Id = i, ConversationId = 1, Role = "user", Content = $"m{i}" });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1, pageSize: 2);

        Rows(doc, "transactions").Select(t => t.GetProperty("id").GetInt32()).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        Rows(doc, "aiConversations").Single().GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("id").GetInt32()).Should().Equal(1, 2, 3, 4, 5);

        using var zip = await ZipAsync(1, pageSize: 2);
        ReadZipEntry(zip, "transactions.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(8);
        ReadZipEntry(zip, "ai_messages.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(6);
    }

    [Fact]
    public async Task Export_InactiveSharedFamilyAccount_IsExcluded_ButOwnInactiveAccountIsKept()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.Member));
        AddAccount(10, 1, familyId: 5, name: "Mine closed").IsActive = false;
        AddAccount(20, 2, familyId: 5, name: "Partner closed").IsActive = false;
        AddAccount(21, 2, familyId: 5, name: "Partner open");
        AddTx(100, 10);
        AddTx(200, 20);
        AddTx(210, 21);
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);

        Rows(doc, "accounts").Select(a => a.GetProperty("id").GetInt32()).Should().BeEquivalentTo(new[] { 10, 21 });
        Rows(doc, "transactions").Select(t => t.GetProperty("id").GetInt32()).Should().BeEquivalentTo(new[] { 100, 210 });
    }

    [Fact]
    public async Task Export_FamilyContextOnAnotherMembersPrivateRows_IsNotExported()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.Member));
        _context.SuggestedSubscriptions.Add(new SuggestedSubscription { Id = 1, UserId = 2, FamilyId = 5, DisplayMerchantName = "PartnerStreaming" });
        _context.AIInsights.Add(new AIInsight { Id = 1, UserId = 2, FamilyId = 5, Title = "PartnerInsight", Message = "x" });
        _context.UserVisualizations.Add(new UserVisualization { Id = 1, UserId = 2, FamilyId = 5, Title = "PartnerChart" });
        await _context.SaveChangesAsync();

        var text = await JsonTextAsync(1);
        using var doc = JsonDocument.Parse(text);

        Rows(doc, "suggestedSubscriptions").Should().BeEmpty();
        Rows(doc, "insights").Should().BeEmpty();
        Rows(doc, "visualizations").Should().BeEmpty();
        text.Should().NotContain("PartnerStreaming").And.NotContain("PartnerInsight").And.NotContain("PartnerChart");
    }

    [Fact]
    public async Task Export_BudgetAndCategory_IncludeRolloverAndCountUpSettings()
    {
        AddUser(1);
        _context.Budgets.Add(new Budget
        {
            Id = 1, UserId = 1, Name = "B", RolloverMode = ChildBudgetRolloverMode.AllocateToSavings, DefaultSavingsGoalId = 7,
        });
        _context.BudgetCategories.Add(new BudgetCategory
        {
            Id = 1, BudgetId = 1, MaxRolloverMultiplier = 1.5m, RolloverOverflowSavingsGoalId = 8,
            LastAllowanceCalculation = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc),
            CountUpRolloverMode = CountUpRolloverMode.TransferToSavings,
        });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1);
        var budget = Rows(doc, "budgets").Single();
        budget.GetProperty("rolloverMode").GetString().Should().Be("AllocateToSavings");
        budget.GetProperty("defaultSavingsGoalId").GetInt32().Should().Be(7);
        var category = Rows(doc, "budgetCategories").Single();
        category.GetProperty("maxRolloverMultiplier").GetDecimal().Should().Be(1.5m);
        category.GetProperty("rolloverOverflowSavingsGoalId").GetInt32().Should().Be(8);
        category.GetProperty("countUpRolloverMode").GetString().Should().Be("TransferToSavings");
        category.TryGetProperty("lastAllowanceCalculation", out _).Should().BeTrue();

        using var zip = await ZipAsync(1);
        ReadZipEntry(zip, "budgets.csv").Should().Contain("rolloverMode");
        ReadZipEntry(zip, "budget_categories.csv").Should().Contain("maxRolloverMultiplier");
    }

    [Fact]
    public async Task Json_MessagesMergeIntoTheRightConversation_AcrossPages_WithInterleavedIds()
    {
        AddUser(1);
        _context.AiConversations.Add(new AiConversation { Id = 1, UserId = 1, SessionId = Guid.NewGuid() });
        _context.AiConversations.Add(new AiConversation { Id = 2, UserId = 1, SessionId = Guid.NewGuid() });
        _context.AiConversations.Add(new AiConversation { Id = 3, UserId = 1, SessionId = Guid.NewGuid() });
        // Ids interleave across conversations; conversation 2 has none.
        var layout = new[] { (1, 1), (2, 3), (3, 1), (4, 3), (5, 1), (6, 3) };
        foreach (var (id, conv) in layout)
            _context.AiConversationMessages.Add(new AiConversationMessage { Id = id, ConversationId = conv, Role = "user", Content = $"m{id}" });
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1, pageSize: 2);

        var convs = Rows(doc, "aiConversations");
        convs.Should().HaveCount(3);
        convs[0].GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetInt32()).Should().Equal(1, 3, 5);
        convs[1].GetProperty("messages").EnumerateArray().Should().BeEmpty();
        convs[2].GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetInt32()).Should().Equal(2, 4, 6);
    }

    [Fact]
    public async Task Paging_SkippedHiddenRowsDoNotEndThePagingEarly()
    {
        AddUser(1, familyId: 5);
        AddUser(2, familyId: 5);
        AddFamily(5, (2, FamilyMemberRole.Owner), (1, FamilyMemberRole.ReadOnly));
        AddAccount(20, 2, familyId: 5);
        for (var i = 1; i <= 4; i++) AddTx(i, 20, $"hidden{i}").HiddenFromReadOnly = true;
        AddTx(5, 20, "visible");
        await _context.SaveChangesAsync();

        using var doc = await JsonAsync(1, pageSize: 2);

        Rows(doc, "transactions").Select(t => t.GetProperty("description").GetString()).Should().Equal("visible");
    }

    // ---- audit ----

    [Fact]
    public async Task RecordExport_WritesAuditRow_WithUserIdAndFormatOnly()
    {
        AddUser(1);
        await _context.SaveChangesAsync();

        await Service().RecordExportAsync(1, "csv");

        var log = await _context.SecurityAuditLogs.SingleAsync();
        log.UserId.Should().Be(1);
        log.EventType.Should().Be("DATA_EXPORT");
        log.AdditionalData.Should().Be("{\"format\":\"csv\"}");
        log.IpAddress.Should().BeNull();
        log.UserAgent.Should().BeNull();
    }
}
