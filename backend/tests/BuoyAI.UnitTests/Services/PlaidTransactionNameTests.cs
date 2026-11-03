using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The description Plaid gives us for a transaction.
///
/// The old chain was `MerchantName ?? OriginalDescription ?? ""`, which never consulted
/// Plaid's `Name` — the only always-populated field. Plaid leaves MerchantName null for
/// checks and account transfers ("no meaningful merchant"), and only sends
/// OriginalDescription when the request opts in via include_original_description, which we
/// never do. So both misses collapsed to "" and the transaction landed with no name at all:
/// 137 of one real user's 716 transactions (19%).
/// </summary>
public class PlaidTransactionNameTests
{
  private static Going.Plaid.Entity.Transaction Tx(
    string? merchantName = null,
    string? name = null,
    string? originalDescription = null,
    string? checkNumber = null,
    string? payee = null,
    string? payer = null) =>
    new()
    {
      MerchantName = merchantName,
      Name = name!,
      OriginalDescription = originalDescription,
      CheckNumber = checkNumber,
      PaymentMeta = (payee == null && payer == null)
        ? null
        : new Going.Plaid.Entity.PaymentMeta { Payee = payee, Payer = payer },
    };

  [Fact]
  public void PlaidDescriptionOf_MerchantNamePresent_PrefersIt()
  {
    // The enriched, human-readable name is the best of the three.
    PlaidService.PlaidDescriptionOf(Tx(merchantName: "Sam's Club", name: "SAMS CLUB #4791"))
      .Should().Be("Sam's Club");
  }

  [Fact]
  public void PlaidDescriptionOf_NoMerchantName_FallsBackToName()
  {
    // The real bug: a check or transfer. Plaid sends no merchant, and no
    // OriginalDescription (we never opt in) — but `name` is always there.
    PlaidService.PlaidDescriptionOf(Tx(merchantName: null, name: "CHECK #1042"))
      .Should().Be("CHECK #1042");
  }

  [Fact]
  public void PlaidDescriptionOf_OnlyOriginalDescription_UsesIt()
  {
    // Only reachable if include_original_description is ever enabled.
    PlaidService.PlaidDescriptionOf(Tx(originalDescription: "ACH CREDIT PAYROLL"))
      .Should().Be("ACH CREDIT PAYROLL");
  }

  [Fact]
  public void PlaidDescriptionOf_NothingUsable_ReturnsEmpty()
  {
    PlaidService.PlaidDescriptionOf(Tx()).Should().BeEmpty();
  }

  // Checks and inter-bank transfers are the two kinds Plaid gives no merchant for — i.e. the
  // ones that arrive nameless. Plaid does describe them, via check_number / payment_meta.

  [Fact]
  public void PlaidDescriptionOf_CheckWithNoOtherName_UsesTheCheckNumber()
  {
    PlaidService.PlaidDescriptionOf(Tx(checkNumber: "1042")).Should().Be("Check #1042");
  }

  [Fact]
  public void PlaidDescriptionOf_TransferWithPayee_NamesTheDestination()
  {
    PlaidService.PlaidDescriptionOf(Tx(payee: "Jane Smith")).Should().Be("Transfer to Jane Smith");
  }

  [Fact]
  public void PlaidDescriptionOf_TransferWithOnlyPayer_NamesTheSource()
  {
    PlaidService.PlaidDescriptionOf(Tx(payer: "ACME Payroll")).Should().Be("Transfer from ACME Payroll");
  }

  [Fact]
  public void PlaidDescriptionOf_RealNamePresent_BeatsTheCheckFallback()
  {
    // The fallbacks are last resort — a real description still wins.
    PlaidService.PlaidDescriptionOf(Tx(name: "CHECK PAID #1042", checkNumber: "1042"))
      .Should().Be("CHECK PAID #1042");
  }

  [Fact]
  public void PlaidDescriptionOf_BlankMerchantName_FallsBackRatherThanReturningBlank()
  {
    // Whitespace/empty must not win — that's how a nameless row is born.
    PlaidService.PlaidDescriptionOf(Tx(merchantName: "   ", name: "CHECK #1042"))
      .Should().Be("CHECK #1042");
  }
}
