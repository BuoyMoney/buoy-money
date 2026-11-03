using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services.SimpleFin;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.SimpleFin;

public class SimpleFinMappingTests
{
    // ── prefixed ids ────────────────────────────────────────────────────

    [Fact]
    public void Ids_StartWithTheProviderPrefix_SoTheyCannotEqualAPlaidId()
    {
        // Plaid ids are bare opaque strings; none of them starts with "sfin:".
        var plaidStyle = "BxBXxLj1m4HMXBm9WZZmCWVbPjX16EHwv99vp";

        SimpleFinMapping.TransactionId(4, "ACT-1", "TRN-1").Should().Be("sfin:4:ACT-1:TRN-1");
        SimpleFinMapping.AccountId(4, "ACT-1").Should().Be("sfin:4:ACT-1");
        SimpleFinMapping.TransactionId(4, "ACT-1", plaidStyle).Should().NotBe(plaidStyle);
    }

    [Fact]
    public void Ids_AreNamespacedByConnectionAndAccount_SoOverlappingBridgeIdsNeverCollide()
    {
        SimpleFinMapping.AccountId(1, "ACT-1").Should().NotBe(SimpleFinMapping.AccountId(2, "ACT-1"));
        SimpleFinMapping.TransactionId(1, "ACT-1", "T").Should().NotBe(SimpleFinMapping.TransactionId(2, "ACT-1", "T"));
        SimpleFinMapping.TransactionId(1, "ACT-1", "T").Should().NotBe(SimpleFinMapping.TransactionId(1, "ACT-2", "T"));
    }

    [Fact]
    public void Ids_ADelimiterInsideAProviderId_CannotMakeTwoDifferentPairsCollide()
    {
        SimpleFinMapping.TransactionId(1, "A:B", "C").Should().NotBe(SimpleFinMapping.TransactionId(1, "A", "B:C"));
    }

    [Fact]
    public void Ids_TheSameRawIdMapsToTheSameStoredId_SoAReconnectFindsTheOldAccount()
    {
        SimpleFinMapping.AccountId(1, "ACT-1").Should().Be(SimpleFinMapping.AccountId(1, "ACT-1"));
        SimpleFinMapping.AccountId(1, " ACT-1 ").Should().Be(SimpleFinMapping.AccountId(1, "ACT-1"));
    }

    [Fact]
    public void Ids_ALongOneIsHashedToFitTheColumn_StillUnique_AndStillPrefixed()
    {
        var a = SimpleFinMapping.TransactionId(1, "ACT-1", new string('a', 200));
        var b = SimpleFinMapping.TransactionId(1, "ACT-1", new string('a', 199) + "b");

        a.Length.Should().BeLessThanOrEqualTo(100);
        a.Should().StartWith("sfin:").And.NotBe(b);
        SimpleFinMapping.TransactionId(1, "ACT-1", new string('a', 200)).Should().Be(a);
    }

    [Fact]
    public void ItemId_IsAFingerprintOfTheAccessUrl_NotTheUrl()
    {
        var id = SimpleFinMapping.ItemId(SimpleFinFixtures.AccessUrl);

        id.Should().StartWith("sfin:").And.HaveLength(5 + 32);
        id.Should().NotContain("demo-pass").And.NotContain("demo-user");
        SimpleFinMapping.ItemId(SimpleFinFixtures.AccessUrl).Should().Be(id);
        SimpleFinMapping.ItemId("https://other:other@bridge.simplefin.org/simplefin").Should().NotBe(id);
    }

    // ── account types ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Everyday Checking", AccountType.Checking)]
    [InlineData("TOTAL CHECKING", AccountType.Checking)]
    [InlineData("Visa Debit Card", AccountType.Checking)]
    [InlineData("Premier Savings", AccountType.Savings)]
    [InlineData("Money Market", AccountType.Savings)]
    [InlineData("Visa Rewards Credit Card", AccountType.CreditCard)]
    [InlineData("Platinum Card", AccountType.CreditCard)]
    [InlineData("Chase Freedom Mastercard", AccountType.CreditCard)]
    [InlineData("Account 4421", AccountType.Checking)]
    [InlineData("", AccountType.Checking)]
    public void GuessAccountType_ReadsTheName(string name, AccountType expected) =>
        SimpleFinMapping.GuessAccountType(name).Should().Be(expected);

    [Theory]
    [InlineData("Traditional IRA")]
    [InlineData("Roth IRA")]
    [InlineData("401k Plan")]
    [InlineData("Brokerage Account")]
    [InlineData("Home Mortgage")]
    [InlineData("Auto Loan")]
    [InlineData("HELOC")]
    public void GuessAccountType_RetirementBrokerageAndLoansAreOutsideTheBudget(string name)
    {
        var type = SimpleFinMapping.GuessAccountType(name);

        AccountTypePolicy.IsBudgetRelevant(type).Should().BeFalse();
    }

    [Fact]
    public void GuessAccountType_AWordThatMerelyContainsIra_IsNotARetirementAccount() =>
        SimpleFinMapping.GuessAccountType("Mirage Checking").Should().Be(AccountType.Checking);

    [Fact]
    public void StoredBalance_FlipsOnlyCardsSoDebtReadsAsPositive()
    {
        SimpleFinMapping.StoredBalance(AccountType.CreditCard, -310.20m).Should().Be(310.20m);
        SimpleFinMapping.StoredBalance(AccountType.Checking, -12m).Should().Be(-12m);
        SimpleFinMapping.StoredBalance(AccountType.Savings, 900m).Should().Be(900m);
    }

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData("EUR", "EUR")]
    [InlineData("", "USD")]
    [InlineData("https://example.com/custom-unit", "USD")]
    public void CurrencyOrDefault_AcceptsOnlyThreeLetterCodes(string input, string expected) =>
        SimpleFinMapping.CurrencyOrDefault(input).Should().Be(expected);

    // ── transactions ────────────────────────────────────────────────────

    [Fact]
    public void ToIncoming_KeepsTheBridgesSign_AndMarksTheRowForReview()
    {
        var account = new SimpleFinAccount("ACT-1", "Checking", "My Bank", "USD", 1m, null, Array.Empty<SimpleFinTransaction>());
        var spend = new SimpleFinTransaction("T1", DateTimeOffset.FromUnixTimeSeconds(1759900000), -45.10m, "  KROGER #123  ", null, false);
        var pay = new SimpleFinTransaction("T2", DateTimeOffset.FromUnixTimeSeconds(1759950000), 2500m, "PAYROLL",
            DateTimeOffset.FromUnixTimeSeconds(1759940000), false);

        var outgoing = SimpleFinMapping.ToIncoming(9, account, spend);
        var incoming = SimpleFinMapping.ToIncoming(9, account, pay);

        outgoing.Provider.Should().Be(BankProvider.SimpleFin);
        outgoing.ExternalTransactionId.Should().Be("sfin:9:ACT-1:T1");
        outgoing.ExternalAccountId.Should().Be("sfin:9:ACT-1");
        outgoing.Amount.Should().Be(-45.10m);
        outgoing.Direction.Should().Be(TransactionType.Debit);
        outgoing.Description.Should().Be("KROGER #123");
        outgoing.OriginalDescription.Should().Be("KROGER #123");
        outgoing.Date.Should().Be(new DateOnly(2025, 10, 8));
        outgoing.IsPending.Should().BeFalse();
        outgoing.CategoryId.Should().Be(SystemCategoryIds.Uncategorized);
        outgoing.NeedsReview.Should().BeTrue();
        outgoing.CategorySource.Should().Be("SimpleFin");

        incoming.Amount.Should().Be(2500m);
        incoming.Direction.Should().Be(TransactionType.Credit);
        incoming.AuthorizedDate.Should().Be(new DateOnly(2025, 10, 8));
    }
}
