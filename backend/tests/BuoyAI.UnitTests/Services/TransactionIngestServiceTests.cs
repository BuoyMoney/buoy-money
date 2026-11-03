using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The provider-neutral ingest, driven with a record that never touched the Plaid SDK. The
/// Plaid-shaped behavior is pinned by PlaidIngestCharacterizationTests.
/// </summary>
public class TransactionIngestServiceTests
{
  private readonly List<Transaction> _store = new();
  private readonly Mock<ITransactionIngestCategorizer> _categorizer = new();
  private readonly TransactionIngestService _svc;
  private int _nextId = 1;

  public TransactionIngestServiceTests()
  {
    var txnRepo = new Mock<ITransactionRepository>();
    txnRepo.Setup(r => r.GetByPlaidTransactionIdAsync(It.IsAny<string>()))
      .ReturnsAsync((string id) => _store.FirstOrDefault(t => t.PlaidTransactionId == id));
    txnRepo.Setup(r => r.CreateManyAsync(It.IsAny<IEnumerable<Transaction>>()))
      .ReturnsAsync((IEnumerable<Transaction> ts) =>
      {
        var list = ts.ToList();
        foreach (var t in list) { t.Id = _nextId++; _store.Add(t); }
        return list;
      });
    var accountRepo = new Mock<IAccountRepository>();
    accountRepo.Setup(r => r.GetByPlaidAccountIdAsync("acct-1")).ReturnsAsync(new Account { Id = 5 });
    _categorizer.Setup(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new IngestCategorizationResult { RulesApplied = 2 });

    _svc = new TransactionIngestService(txnRepo.Object, accountRepo.Object, _categorizer.Object,
      Mock.Of<ILogger<TransactionIngestService>>());
  }

  private static IncomingTransaction Tx(string id, string? pendingId = null, decimal amount = -12.34m) => new()
  {
    Provider = BankProvider.Mx,
    ExternalTransactionId = id,
    ExternalAccountId = "acct-1",
    PendingExternalTransactionId = pendingId,
    Amount = amount,
    Direction = amount < 0 ? TransactionType.Debit : TransactionType.Credit,
    Date = new DateOnly(2026, 10, 1),
    Description = "Corner Cafe",
    MerchantName = "Corner Cafe",
    CategoryId = 4,
    NeedsReview = true,
    CategorySource = "Mx-NeedsReview",
  };

  [Fact]
  public async Task NewTransaction_IsCreatedWithNeutralFieldsAndCategorized()
  {
    var result = await _svc.IngestAsync(new[] { Tx("m1") });

    var row = result.Created.Should().ContainSingle().Subject;
    row.PlaidTransactionId.Should().Be("m1");
    row.AccountId.Should().Be(5);
    row.Amount.Should().Be(-12.34m);
    row.Type.Should().Be(TransactionType.Debit);
    row.NeedsReview.Should().BeTrue();
    row.CategorySource.Should().Be("Mx-NeedsReview");
    row.Date.Should().Be(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
    result.Categorization.RulesApplied.Should().Be(2);
    _categorizer.Verify(c => c.ProcessIngestedAsync(It.Is<IReadOnlyCollection<Transaction>>(l => l.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task NeedsReviewCount_IsTakenBeforeCategorizationClearsTheFlag()
  {
    _categorizer.Setup(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()))
      .Callback((IReadOnlyCollection<Transaction> ts, CancellationToken _) =>
      {
        foreach (var t in ts) t.NeedsReview = false; // what a matching user rule does
      })
      .ReturnsAsync(new IngestCategorizationResult());

    var result = await _svc.IngestAsync(new[] { Tx("m1"), Tx("m2") });

    result.Created.Should().OnlyContain(t => !t.NeedsReview);
    result.NeedsReviewCount.Should().Be(2);
  }

  [Fact]
  public async Task SecondIngestOfTheSameId_CreatesNothing()
  {
    await _svc.IngestAsync(new[] { Tx("m1") });
    var again = await _svc.IngestAsync(new[] { Tx("m1") });

    again.Created.Should().BeEmpty();
    _store.Should().HaveCount(1);
  }

  [Fact]
  public async Task PostedChargeWithPendingLink_PromotesTheHold_AndSkipsCategorization()
  {
    await _svc.IngestAsync(new[] { Tx("hold", amount: -10m) });
    _store[0].IsPending = true;
    _categorizer.Invocations.Clear();

    var result = await _svc.IngestAsync(new[] { Tx("posted", pendingId: "hold", amount: -11m) });

    result.Created.Should().BeEmpty();
    result.MergedPendingCount.Should().Be(1);
    _store.Should().ContainSingle().Which.Should().Match<Transaction>(t =>
      t.PlaidTransactionId == "posted" && !t.IsPending && t.Amount == -11m);
    _categorizer.Verify(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task BlankIdAndUnknownAccount_AreDropped()
  {
    var result = await _svc.IngestAsync(new[]
    {
      Tx(""),
      Tx("m2") with { ExternalAccountId = "nope" },
      Tx("m3") with { ExternalAccountId = null },
    });

    result.Created.Should().BeEmpty();
    _store.Should().BeEmpty();
  }

  [Fact]
  public async Task EmptyInput_TouchesNothing()
  {
    var result = await _svc.IngestAsync(Array.Empty<IncomingTransaction>());

    result.Created.Should().BeEmpty();
    _categorizer.VerifyNoOtherCalls();
  }
}
