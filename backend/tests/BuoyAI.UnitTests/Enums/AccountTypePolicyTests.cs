using BuoyAI.Core.Enums;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Enums;

/// <summary>
/// The provider-neutral budget-relevance policy (#310): deposit accounts + credit cards are
/// aggregated; investment/brokerage and loans are excluded so they never surface.
/// </summary>
public class AccountTypePolicyTests
{
    [Theory]
    [InlineData(AccountType.Checking)]
    [InlineData(AccountType.Savings)]
    [InlineData(AccountType.CreditCard)]
    [InlineData(AccountType.Cash)] // synthetic manual-entry account — still a budget account
    public void IsBudgetRelevant_True_ForDepositCreditAndCash(AccountType type)
    {
        AccountTypePolicy.IsBudgetRelevant(type).Should().BeTrue();
    }

    [Theory]
    [InlineData(AccountType.Investment)] // brokerage/retirement, e.g. Robinhood — out of scope
    [InlineData(AccountType.Other)]      // loans/mortgages/unmapped land here
    public void IsBudgetRelevant_False_ForInvestmentAndOther(AccountType type)
    {
        AccountTypePolicy.IsBudgetRelevant(type).Should().BeFalse();
    }
}
