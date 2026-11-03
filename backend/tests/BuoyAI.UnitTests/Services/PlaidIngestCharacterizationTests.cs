using System.Reflection;
using System.Text.Json;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using PlaidTx = Going.Plaid.Entity.Transaction;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Characterization of what the Plaid transaction ingest persists. It drives the real
/// PlaidService against in-memory fake repositories and snapshots every persisted field, so
/// the provider-neutral ingest refactor can be proven to produce the same Transaction rows.
/// The three sync loops share one decision tree (dedupe by external id, merge a pending hold
/// into its posted charge, create, categorize); the loop reachable without the Plaid network
/// client (historical import) is the one driven here.
/// </summary>
public class PlaidIngestCharacterizationTests
{
  internal static readonly string Nl = new string((char)10, 1);
  private readonly List<Transaction> _store = new();
  private readonly List<Transaction> _created = new();
  private readonly List<Transaction> _updated = new();
  private readonly List<int> _deleted = new();
  private readonly List<List<string>> _categorizerBatches = new();
  private int _nextId = 100;

  private static readonly Dictionary<string, int> Accounts = new()
  {
    ["acc-checking"] = 7,
    ["acc-card"] = 8,
  };

  private static IConfiguration Config() => new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Plaid:ClientId"] = "test-client",
      ["Plaid:SecretKey"] = "test-secret",
      ["Plaid:Environment"] = "sandbox",
    })
    .Build();

  private PlaidService CreateService(ITransactionIngestCategorizer? categorizerOverride = null)
  {
    var txnRepo = new Mock<ITransactionRepository>();
    txnRepo.Setup(r => r.GetByPlaidTransactionIdAsync(It.IsAny<string>()))
      .ReturnsAsync((string id) => _store.FirstOrDefault(t => t.PlaidTransactionId == id));
    txnRepo.Setup(r => r.CreateManyAsync(It.IsAny<IEnumerable<Transaction>>()))
      .ReturnsAsync((IEnumerable<Transaction> ts) =>
      {
        var list = ts.ToList();
        foreach (var t in list)
        {
          t.Id = _nextId++;
          _store.Add(t);
          _created.Add(t);
        }
        return list;
      });
    txnRepo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
      .ReturnsAsync((Transaction t) => { _updated.Add(t); return t; });
    txnRepo.Setup(r => r.DeleteAsync(It.IsAny<int>()))
      .Returns((int id) => { _deleted.Add(id); _store.RemoveAll(t => t.Id == id); return Task.CompletedTask; });

    var accountRepo = new Mock<IAccountRepository>();
    accountRepo.Setup(r => r.GetByPlaidAccountIdAsync(It.IsAny<string>()))
      .ReturnsAsync((string id) => Accounts.TryGetValue(id, out var pk) ? new Account { Id = pk } : null);

    ITransactionIngestCategorizer categorizer;
    if (categorizerOverride != null)
    {
      categorizer = categorizerOverride;
    }
    else
    {
      var mock = new Mock<ITransactionIngestCategorizer>();
      mock.Setup(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((IReadOnlyCollection<Transaction> ts, CancellationToken _) =>
        {
          _categorizerBatches.Add(ts.Select(t => t.PlaidTransactionId).ToList());
          return new IngestCategorizationResult();
        });
      categorizer = mock.Object;
    }

    return PlaidServiceFactory.Create(
      Config(), txnRepo.Object, accountRepo.Object, categorizer,
      new BuoyAiMetrics(new TestMeterFactory()));
  }

  private static Task Process(PlaidService svc, IReadOnlyList<PlaidTx> txns)
  {
    var m = typeof(PlaidService).GetMethod("ProcessTransactionsResponse",
      BindingFlags.NonPublic | BindingFlags.Instance)!;
    return (Task)m.Invoke(svc, new object[] { txns })!;
  }

  private static PlaidTx Rich(
    string id,
    string account = "acc-checking",
    decimal amount = 42.5m,
    string? pendingId = null,
    bool pending = false,
    string? merchant = "Blue Bottle",
    string confidence = "VERY_HIGH") => new()
  {
    TransactionId = id,
    AccountId = account,
    PendingTransactionId = pendingId,
    Amount = amount,
    Date = new DateOnly(2026, 9, 3),
    AuthorizedDate = new DateOnly(2026, 9, 2),
    Name = "SQ *BLUE BOTTLE 123",
    MerchantName = merchant,
    OriginalDescription = "SQ *BLUE BOTTLE 123 OAKLAND CA",
    IsoCurrencyCode = "USD",
    Pending = pending,
    PaymentChannel = Going.Plaid.Entity.TransactionPaymentChannelEnum.InStore,
    LogoUrl = "https://logo/bb.png",
    MerchantEntityId = "ent-1",
    CheckNumber = "1042",
    PersonalFinanceCategory = new Going.Plaid.Entity.PersonalFinanceCategory
    {
      Primary = "FOOD_AND_DRINK",
      Detailed = "FOOD_AND_DRINK_COFFEE",
      ConfidenceLevel = confidence,
    },
    PersonalFinanceCategoryIconUrl = "https://icon/food.png",
    Location = new Going.Plaid.Entity.Location { City = "Oakland", Region = "CA", StoreNumber = "12" },
    PaymentMeta = new Going.Plaid.Entity.PaymentMeta
    {
      Payee = "Payee X", Payer = "Payer Y", Reason = "why", ReferenceNumber = "ref-9",
    },
    Counterparties = new List<Going.Plaid.Entity.Counterparty>
    {
      new()
      {
        Name = "Blue Bottle Coffee", EntityId = "cp-1", LogoUrl = "https://logo/cp.png",
        Website = "bluebottle.com", Type = Going.Plaid.Entity.CounterpartyType.Merchant,
        ConfidenceLevel = "HIGH",
      },
    },
  };

  internal static string Snapshot(IEnumerable<Transaction> rows)
  {
    static bool Simple(Type t)
    {
      var u = Nullable.GetUnderlyingType(t) ?? t;
      return u.IsPrimitive || u.IsEnum || u == typeof(string) || u == typeof(decimal) || u == typeof(DateTime);
    }

    var projected = rows.Select(t => typeof(Transaction)
      .GetProperties(BindingFlags.Public | BindingFlags.Instance)
      .Where(p => Simple(p.PropertyType) && p.Name != "Id")
      .OrderBy(p => p.Name, StringComparer.Ordinal)
      .ToDictionary(p => p.Name, p => Convert.ToString(p.GetValue(t), System.Globalization.CultureInfo.InvariantCulture) ?? "~null"));
    var json = JsonSerializer.Serialize(projected, new JsonSerializerOptions { WriteIndented = true });
    return json.ReplaceLineEndings(Nl);
  }

  [Fact]
  public async Task NewRichTransaction_IsCreatedWithEveryEnrichedField_AndCategorized()
  {
    var svc = CreateService();
    await Process(svc, new[] { Rich("t-new") });

    _created.Should().HaveCount(1);
    _updated.Should().BeEmpty();
    _categorizerBatches.Should().ContainSingle().Which.Should().Equal("t-new");
    Snapshot(_created).Should().Be(PlaidIngestGolden.NewRich.ReplaceLineEndings(Nl));
  }

  [Fact]
  public async Task NullMerchantNullDateCredit_IsCreatedWithFallbacks()
  {
    var svc = CreateService();
    await Process(svc, new[]
    {
      new PlaidTx { TransactionId = "t-credit", AccountId = "acc-card", Amount = -1200m, Name = "PAYROLL ACME" },
    });

    Snapshot(_created).Should().Be(PlaidIngestGolden.NullMerchantCredit.ReplaceLineEndings(Nl));
  }

  [Fact]
  public async Task Duplicate_UnknownAccount_AndEmptyId_AreSkipped()
  {
    _store.Add(new Transaction { Id = 1, PlaidTransactionId = "t-dup", Amount = -5m, AccountId = 7 });
    var svc = CreateService();
    await Process(svc, new[]
    {
      Rich("t-dup"),
      Rich("t-orphan", account: "acc-unknown"),
      new PlaidTx { TransactionId = "", AccountId = "acc-checking", Amount = 1m },
      new PlaidTx { TransactionId = "t-noacct", AccountId = null, Amount = 1m },
    });

    _created.Should().BeEmpty();
    _updated.Should().BeEmpty();
    _categorizerBatches.Should().BeEmpty();
    _store.Should().ContainSingle().Which.Amount.Should().Be(-5m);
  }

  [Fact]
  public async Task PostedChargeWithPendingLink_PromotesTheHoldInPlace()
  {
    var hold = new Transaction
    {
      Id = 1, PlaidTransactionId = "t-hold", AccountId = 7, IsPending = true, Amount = -30m,
      Date = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
      MerchantName = "Old Name", Description = "Old Name", Type = Core.Enums.TransactionType.Debit,
      UserNotes = "kept", CategoryId = 3,
    };
    _store.Add(hold);
    var svc = CreateService();

    var posted = Rich("t-posted", amount: 31.25m, pendingId: "t-hold");
    await Process(svc, new[] { posted });

    _created.Should().BeEmpty();
    _updated.Should().ContainSingle().Which.Should().BeSameAs(hold);
    _categorizerBatches.Should().BeEmpty();
    Snapshot(_updated).Should().Be(PlaidIngestGolden.PromotedHold.ReplaceLineEndings(Nl));
  }

  [Fact]
  public async Task PostedChargeWhosePendingIdIsUnknown_IsCreatedAsNew()
  {
    var svc = CreateService();
    var posted = Rich("t-posted2", pendingId: "t-missing-hold");
    await Process(svc, new[] { posted });

    _created.Should().ContainSingle().Which.PlaidTransactionId.Should().Be("t-posted2");
    _updated.Should().BeEmpty();
  }

  [Fact]
  public async Task PendingTransaction_IsCreatedPending_AndMediumConfidenceNeedsReview()
  {
    var svc = CreateService();
    var pending = Rich("t-pend", pending: true, confidence: "MEDIUM");
    await Process(svc, new[] { pending });

    Snapshot(_created).Should().Be(PlaidIngestGolden.PendingMedium.ReplaceLineEndings(Nl));
  }

  [Fact]
  public async Task MixedBatch_CreatesMergesAndKeepsBatchDuplicatesAsSeparateRows()
  {
    var hold = new Transaction
    {
      Id = 1, PlaidTransactionId = "h1", AccountId = 8, IsPending = true, Amount = -10m,
      Date = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), MerchantName = "M",
    };
    _store.Add(hold);
    var svc = CreateService();

    var merge = Rich("p1", pendingId: "h1", merchant: null);
    await Process(svc, new[] { Rich("n1"), merge, Rich("n1"), Rich("n2", "acc-card") });

    // The same external id twice in one page is not deduped within the batch (rows are only
    // visible to the lookup after CreateManyAsync). Both rows are created today.
    _created.Select(t => t.PlaidTransactionId).Should().Equal("n1", "n1", "n2");
    _updated.Should().ContainSingle().Which.PlaidTransactionId.Should().Be("p1");
    _updated[0].MerchantName.Should().Be("M");
    _categorizerBatches.Should().ContainSingle().Which.Should().Equal("n1", "n1", "n2");
  }

  [Fact]
  public async Task CategorizerThrowing_DoesNotFailTheIngest_AndRowsStayCreated()
  {
    var categorizer = new Mock<ITransactionIngestCategorizer>();
    categorizer.Setup(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("boom"));
    var svc = CreateService(categorizer.Object);

    var act = () => Process(svc, new[] { Rich("x1") });

    await act.Should().NotThrowAsync();
    _created.Should().ContainSingle();
  }

  [Fact]
  public async Task RemovedTransactions_DeleteUntouchedRows_AndKeepUserTouchedOnes()
  {
    _store.Add(new Transaction { Id = 1, PlaidTransactionId = "r-clean", AccountId = 7 });
    _store.Add(new Transaction { Id = 2, PlaidTransactionId = "r-touched", AccountId = 7, UserNotes = "mine" });
    var svc = CreateService();

    var removed = await svc.RemoveTransactionsAsync(new[] { "r-clean", "r-touched", "r-clean", "", "r-gone" });

    removed.Should().Be(1);
    _deleted.Should().Equal(1);
    _store.Should().ContainSingle().Which.PlaidTransactionId.Should().Be("r-touched");
  }
}

/// <summary>
/// One place that builds a PlaidService for tests, so constructor changes touch a single file.
/// </summary>
internal static class PlaidServiceFactory
{
  public static PlaidService Create(
    IConfiguration config,
    ITransactionRepository txnRepo,
    IAccountRepository accountRepo,
    ITransactionIngestCategorizer categorizer,
    BuoyAiMetrics metrics)
  {
    return new PlaidService(
      config,
      Mock.Of<IUserRepository>(),
      accountRepo,
      txnRepo,
      Mock.Of<IPlaidConnectionRepository>(),
      Mock.Of<INotificationService>(),
      Mock.Of<IBankConnectionAlertService>(),
      Mock.Of<IEncryptionService>(),
      Mock.Of<IPlaidUsageTracker>(),
      new TransactionIngestService(txnRepo, accountRepo, categorizer, Mock.Of<ILogger<TransactionIngestService>>()),
      Mock.Of<ILogger<PlaidService>>(),
      metrics);
  }
}

internal static class PlaidIngestGolden
{
  public const string NewRich = """
[
  {
    "AICategorizationDate": "",
    "AIConfidence": "",
    "AISuggestedCategoryId": "",
    "AccountId": "7",
    "Amount": "-42.5",
    "AuthorizedDate": "09/02/2026 00:00:00",
    "BudgetId": "",
    "CategoryId": "21",
    "CategorySource": "Plaid",
    "CheckNumber": "1042",
    "ClaimedByUserId": "",
    "CoveringTransferTransactionId": "",
    "CreatedAt": "01/01/0001 00:00:00",
    "Date": "09/03/2026 00:00:00",
    "Description": "Blue Bottle",
    "DisplayName": "Blue Bottle",
    "HiddenFromReadOnly": "False",
    "ImportBatchId": "",
    "IsPending": "False",
    "IsSplit": "False",
    "IsUserCategorized": "False",
    "IsoCurrencyCode": "USD",
    "LocationCity": "Oakland",
    "LocationRegion": "CA",
    "LocationStoreNumber": "12",
    "MerchantName": "Blue Bottle",
    "NeedsReview": "False",
    "Nickname": "",
    "OriginalDescription": "SQ *BLUE BOTTLE 123 OAKLAND CA",
    "ParentTransactionId": "",
    "PaymentPayee": "Payee X",
    "PaymentPayer": "Payer Y",
    "PaymentReason": "why",
    "PaymentReferenceNumber": "ref-9",
    "PersonalUserCategoryId": "",
    "PlaidCategoryConfidence": "VERY_HIGH",
    "PlaidCategoryDetailed": "FOOD_AND_DRINK_COFFEE",
    "PlaidCategoryIconUrl": "https://icon/food.png",
    "PlaidCategoryPrimary": "FOOD_AND_DRINK",
    "PlaidCounterpartyConfidence": "HIGH",
    "PlaidCounterpartyType": "Merchant",
    "PlaidMerchantEntityId": "ent-1",
    "PlaidMerchantLogoUrl": "https://logo/bb.png",
    "PlaidMerchantWebsite": "bluebottle.com",
    "PlaidPaymentChannel": "InStore",
    "PlaidTransactionId": "t-new",
    "SavingsGoalId": "",
    "SemanticTags": "food, drink, coffee",
    "SkipUntil": "",
    "TransactionCode": "",
    "TransferReviewedAt": "",
    "Type": "Debit",
    "UpdatedAt": "01/01/0001 00:00:00",
    "UserNotes": ""
  }
]
""";
  public const string NullMerchantCredit = """
[
  {
    "AICategorizationDate": "",
    "AIConfidence": "",
    "AISuggestedCategoryId": "",
    "AccountId": "8",
    "Amount": "1200",
    "AuthorizedDate": "",
    "BudgetId": "",
    "CategoryId": "99",
    "CategorySource": "Plaid-NeedsReview",
    "CheckNumber": "",
    "ClaimedByUserId": "",
    "CoveringTransferTransactionId": "",
    "CreatedAt": "01/01/0001 00:00:00",
    "Date": "01/01/0001 00:00:00",
    "Description": "PAYROLL ACME",
    "DisplayName": "PAYROLL ACME",
    "HiddenFromReadOnly": "False",
    "ImportBatchId": "",
    "IsPending": "False",
    "IsSplit": "False",
    "IsUserCategorized": "False",
    "IsoCurrencyCode": "",
    "LocationCity": "",
    "LocationRegion": "",
    "LocationStoreNumber": "",
    "MerchantName": "",
    "NeedsReview": "True",
    "Nickname": "",
    "OriginalDescription": "",
    "ParentTransactionId": "",
    "PaymentPayee": "",
    "PaymentPayer": "",
    "PaymentReason": "",
    "PaymentReferenceNumber": "",
    "PersonalUserCategoryId": "",
    "PlaidCategoryConfidence": "UNKNOWN",
    "PlaidCategoryDetailed": "",
    "PlaidCategoryIconUrl": "",
    "PlaidCategoryPrimary": "",
    "PlaidCounterpartyConfidence": "UNKNOWN",
    "PlaidCounterpartyType": "",
    "PlaidMerchantEntityId": "",
    "PlaidMerchantLogoUrl": "",
    "PlaidMerchantWebsite": "",
    "PlaidPaymentChannel": "",
    "PlaidTransactionId": "t-credit",
    "SavingsGoalId": "",
    "SemanticTags": "",
    "SkipUntil": "",
    "TransactionCode": "",
    "TransferReviewedAt": "",
    "Type": "Credit",
    "UpdatedAt": "01/01/0001 00:00:00",
    "UserNotes": ""
  }
]
""";
  public const string PromotedHold = """
[
  {
    "AICategorizationDate": "",
    "AIConfidence": "",
    "AISuggestedCategoryId": "",
    "AccountId": "7",
    "Amount": "-31.25",
    "AuthorizedDate": "",
    "BudgetId": "",
    "CategoryId": "3",
    "CategorySource": "",
    "CheckNumber": "",
    "ClaimedByUserId": "",
    "CoveringTransferTransactionId": "",
    "CreatedAt": "01/01/0001 00:00:00",
    "Date": "09/03/2026 00:00:00",
    "Description": "Blue Bottle",
    "DisplayName": "Blue Bottle",
    "HiddenFromReadOnly": "False",
    "ImportBatchId": "",
    "IsPending": "False",
    "IsSplit": "False",
    "IsUserCategorized": "False",
    "IsoCurrencyCode": "",
    "LocationCity": "",
    "LocationRegion": "",
    "LocationStoreNumber": "",
    "MerchantName": "Blue Bottle",
    "NeedsReview": "False",
    "Nickname": "",
    "OriginalDescription": "",
    "ParentTransactionId": "",
    "PaymentPayee": "",
    "PaymentPayer": "",
    "PaymentReason": "",
    "PaymentReferenceNumber": "",
    "PersonalUserCategoryId": "",
    "PlaidCategoryConfidence": "",
    "PlaidCategoryDetailed": "",
    "PlaidCategoryIconUrl": "",
    "PlaidCategoryPrimary": "",
    "PlaidCounterpartyConfidence": "",
    "PlaidCounterpartyType": "",
    "PlaidMerchantEntityId": "",
    "PlaidMerchantLogoUrl": "",
    "PlaidMerchantWebsite": "",
    "PlaidPaymentChannel": "",
    "PlaidTransactionId": "t-posted",
    "SavingsGoalId": "",
    "SemanticTags": "",
    "SkipUntil": "",
    "TransactionCode": "",
    "TransferReviewedAt": "",
    "Type": "Debit",
    "UpdatedAt": "01/01/0001 00:00:00",
    "UserNotes": "kept"
  }
]
""";
  public const string PendingMedium = """
[
  {
    "AICategorizationDate": "",
    "AIConfidence": "",
    "AISuggestedCategoryId": "",
    "AccountId": "7",
    "Amount": "-42.5",
    "AuthorizedDate": "09/02/2026 00:00:00",
    "BudgetId": "",
    "CategoryId": "21",
    "CategorySource": "Plaid-NeedsReview",
    "CheckNumber": "1042",
    "ClaimedByUserId": "",
    "CoveringTransferTransactionId": "",
    "CreatedAt": "01/01/0001 00:00:00",
    "Date": "09/03/2026 00:00:00",
    "Description": "Blue Bottle",
    "DisplayName": "Blue Bottle",
    "HiddenFromReadOnly": "False",
    "ImportBatchId": "",
    "IsPending": "True",
    "IsSplit": "False",
    "IsUserCategorized": "False",
    "IsoCurrencyCode": "USD",
    "LocationCity": "Oakland",
    "LocationRegion": "CA",
    "LocationStoreNumber": "12",
    "MerchantName": "Blue Bottle",
    "NeedsReview": "True",
    "Nickname": "",
    "OriginalDescription": "SQ *BLUE BOTTLE 123 OAKLAND CA",
    "ParentTransactionId": "",
    "PaymentPayee": "Payee X",
    "PaymentPayer": "Payer Y",
    "PaymentReason": "why",
    "PaymentReferenceNumber": "ref-9",
    "PersonalUserCategoryId": "",
    "PlaidCategoryConfidence": "MEDIUM",
    "PlaidCategoryDetailed": "FOOD_AND_DRINK_COFFEE",
    "PlaidCategoryIconUrl": "https://icon/food.png",
    "PlaidCategoryPrimary": "FOOD_AND_DRINK",
    "PlaidCounterpartyConfidence": "HIGH",
    "PlaidCounterpartyType": "Merchant",
    "PlaidMerchantEntityId": "ent-1",
    "PlaidMerchantLogoUrl": "https://logo/bb.png",
    "PlaidMerchantWebsite": "bluebottle.com",
    "PlaidPaymentChannel": "InStore",
    "PlaidTransactionId": "t-pend",
    "SavingsGoalId": "",
    "SemanticTags": "food, drink, coffee",
    "SkipUntil": "",
    "TransactionCode": "",
    "TransferReviewedAt": "",
    "Type": "Debit",
    "UpdatedAt": "01/01/0001 00:00:00",
    "UserNotes": ""
  }
]
""";
}
