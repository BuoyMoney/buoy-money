using System.Net;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.SimpleFin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuoyAI.UnitTests.SimpleFin;

/// <summary>
/// The real SimpleFIN aggregator, client, ingest service and repositories over an in-memory
/// database. Only the bridge (a fake HTTP handler), encryption (a visible stand-in), the
/// categorizer and the alert service are replaced.
/// </summary>
internal sealed class SimpleFinHarness : IDisposable
{
    public const int UserId = 1;

    private readonly bool _ownsDb;

    public BuoyAIDbContext Db { get; }
    public SimpleFinAggregator Aggregator { get; }
    public FakeBridgeHandler Bridge { get; }
    public Mock<IBankConnectionAlertService> Alerts { get; } = new();
    public Mock<ITransactionIngestCategorizer> Categorizer { get; } = new();

    /// <summary>What the bridge returns for /accounts. Change between calls to script a sequence.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> OnAccounts { get; set; } =
        _ => Json(SimpleFinFixtures.OneBank);

    /// <summary>What the bridge returns for the claim.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> OnClaim { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SimpleFinFixtures.AccessUrl) };

    /// <param name="db">A context over a real database. When null, an in-memory one is used. The harness does not dispose a context it was given.</param>
    public SimpleFinHarness(Action<SimpleFinOptions>? tweakOptions = null, int? familyId = null, BuoyAIDbContext? db = null)
    {
        _ownsDb = db == null;
        Db = db ?? new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

        Db.Users.Add(new User
        {
            Id = UserId, Username = "kat", Email = "kat@home.test", FirstName = "Kat",
            PasswordHash = "x", FamilyId = familyId,
        });
        Db.SaveChanges();

        Categorizer
            .Setup(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestCategorizationResult());

        Bridge = new FakeBridgeHandler(request =>
            request.Method == HttpMethod.Post ? OnClaim(request) : OnAccounts(request));

        var options = SimpleFinTestFactory.Options(tweakOptions);
        var accounts = new AccountRepository(Db);
        var transactions = new TransactionRepository(Db);
        var ingest = new TransactionIngestService(
            transactions, accounts, Categorizer.Object, NullLogger<TransactionIngestService>.Instance);

        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(p => "enc::" + p);
        encryption.Setup(e => e.Decrypt(It.IsAny<string>())).Returns<string>(c => c.StartsWith("enc::") ? c[5..] : string.Empty);

        Aggregator = new SimpleFinAggregator(
            SimpleFinTestFactory.Client(Bridge, options),
            Microsoft.Extensions.Options.Options.Create(options),
            new PlaidConnectionRepository(Db),
            accounts,
            new UserRepository(Db),
            encryption.Object,
            ingest,
            Alerts.Object,
            NullLogger<SimpleFinAggregator>.Instance);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    /// <summary>A SimpleFIN connection already in the database, as the first connect would have left it.</summary>
    public async Task<PlaidConnection> SeedConnectionAsync(Action<PlaidConnection>? tweak = null)
    {
        var connection = new PlaidConnection
        {
            UserId = UserId,
            Provider = BankProvider.SimpleFin,
            AccessToken = "enc::" + SimpleFinFixtures.AccessUrl,
            ItemId = SimpleFinMapping.ItemId(SimpleFinFixtures.AccessUrl),
            InstitutionId = "simplefin",
            InstitutionName = "SimpleFIN",
            IsActive = true,
            Status = PlaidItemStatus.Active,
            ImportStatus = ImportStatus.Completed,
            LastSyncAt = DateTime.UtcNow,
        };
        tweak?.Invoke(connection);
        Db.PlaidConnections.Add(connection);
        await Db.SaveChangesAsync();
        return connection;
    }

    public Task<PlaidConnection> ReloadAsync(int connectionId) =>
        Db.PlaidConnections.AsNoTracking().SingleAsync(c => c.Id == connectionId);

    public void Dispose()
    {
        if (_ownsDb) Db.Dispose();
    }
}
