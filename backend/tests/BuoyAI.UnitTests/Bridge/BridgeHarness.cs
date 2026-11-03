using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.Bridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>One page of what Plaid's /transactions/sync would return, as the Cloud passes it through.</summary>
internal sealed class FakePage
{
    public JsonArray Accounts { get; set; } = new();
    public JsonArray Added { get; set; } = new();
    public JsonArray Modified { get; set; } = new();
    public JsonArray Removed { get; set; } = new();
    public bool HasMore { get; set; }
}

internal sealed class FakeCloudItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; } = "First Platypus Bank";
    public string Status { get; set; } = "active";
    public bool UpdatesAvailable { get; set; } = true;
    public bool OverCap { get; set; }
    public string? Notice { get; set; }
    public DateTime? NoticeAt { get; set; }
    public bool Removed { get; set; }
    public Queue<FakePage> Pages { get; } = new();
    public int Pulls { get; set; }
}

/// <summary>
/// A stand-in for the BuoyAI Cloud bridge that keeps the same promises the real one tests: it checks the credential and
/// the request signature on every call, seals batches to the key the server registered, and moves a page forward only on ack.
/// </summary>
internal sealed class FakeCloud : HttpMessageHandler
{
    public const string Credential = "bmk_0123456789abcdef0123456789abcdef_ZmFrZS1zZWNyZXQtZm9yLXRlc3RzLW9ubHktMDEyMzQ1Ng";
    public static readonly Guid ServerId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

    private ECDsa? _signingKey;
    private byte[]? _encryptionPoint;
    private readonly Dictionary<Guid, (FakeCloudItem Item, string CursorAfter)> _pending = new();

    public List<FakeCloudItem> Items { get; } = new();
    public List<string> Calls { get; } = new();
    public int BadSignatures { get; private set; }
    public bool FailNextAck { get; set; }
    public bool RejectEverything { get; set; }
    public bool Unreachable { get; set; }
    public int? FailRemoveWith { get; set; }

    /// <summary>What GET /bridge/v1/billing answers (null: the Cloud has no billing, 404).</summary>
    public JsonObject? BillingSummary { get; set; }
    public List<string> PortalBodies { get; } = new();
    public Guid? LastBatchId { get; private set; }
    public string? LastSealed { get; private set; }
    public Guid? ForeignServerForSealing { get; set; }
    public byte[]? ForeignKeyPoint { get; set; }

    /// <summary>The bank the next new link page will make (null: the page has not finished).</summary>
    public FakeCloudItem? NextLinkItem { get; set; }

    /// <summary>After this many successful pulls, every pull is refused with 429 and Retry-After: 120.</summary>
    public int? ThrottleAfterPulls { get; set; }

    private int _sessions;
    private readonly Dictionary<string, (Guid? Reconnect, FakeCloudItem? Item)> _links = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable) throw new HttpRequestException("down");
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Calls.Add($"{request.Method} {path}");

        if (path == "/v1/pairing/pair")
        {
            var json = JsonNode.Parse(body)!;
            if (json["code"]!.GetValue<string>() != "ABCDE-FGHJK")
                return Json(HttpStatusCode.BadRequest, new { error = "invalid_code", message = "That pairing code is wrong, used or expired." });
            _encryptionPoint = Convert.FromBase64String(json["encryptionPublicKey"]!.GetValue<string>());
            var sp = Convert.FromBase64String(json["signingPublicKey"]!.GetValue<string>());
            _signingKey = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = sp[1..33], Y = sp[33..65] } });
            return Json(HttpStatusCode.OK, new { serverId = ServerId, credential = Credential, needsConfirmation = true });
        }

        if (RejectEverything) return Json(HttpStatusCode.Unauthorized, new { error = "unauthorized" });
        if (request.Headers.Authorization?.ToString() != "Bearer " + Credential) return Json(HttpStatusCode.Unauthorized, new { error = "unauthorized" });
        if (!SignatureOk(request, body)) { BadSignatures++; return Json(HttpStatusCode.Unauthorized, new { error = "unauthorized" }); }

        if (request.Method == HttpMethod.Get && path == "/bridge/v1/items")
        {
            return Json(HttpStatusCode.OK, Items.Where(i => !i.Removed).Select(i => new
            {
                id = i.Id, institutionName = i.Name, status = i.Status, updatesAvailable = i.UpdatesAvailable, initialUpdateComplete = true,
                historicalUpdateComplete = true, createdAt = DateTime.UtcNow, lastDeliveredAt = (DateTime?)null, overCap = i.OverCap,
                notice = i.Notice, noticeAt = i.NoticeAt
            }));
        }

        if (request.Method == HttpMethod.Get && path == "/bridge/v1/billing")
        {
            return BillingSummary == null
                ? Json(HttpStatusCode.NotFound, new { error = "billing_not_configured", message = "Billing is not set up on this Cloud." })
                : Json(HttpStatusCode.OK, BillingSummary);
        }

        if (request.Method == HttpMethod.Post && path == "/bridge/v1/billing/portal-session")
        {
            PortalBodies.Add(System.Text.Encoding.UTF8.GetString(body));
            return Json(HttpStatusCode.OK, new { url = "https://billing.stripe.test/p/abc" });
        }

        if (request.Method == HttpMethod.Post && path == "/bridge/v1/link/start")
        {
            var reconnect = body.Length > 0 ? JsonNode.Parse(body)?["itemId"]?.GetValue<Guid>() : null;
            var sessionId = "s" + ++_sessions;
            _links[sessionId] = (reconnect, reconnect == null ? NextLinkItem : Items.FirstOrDefault(i => i.Id == reconnect));
            NextLinkItem = null;
            return Json(HttpStatusCode.OK, new { sessionId, url = "https://cloud.test/link/" + sessionId, expiresAt = DateTime.UtcNow.AddMinutes(30) });
        }

        if (request.Method == HttpMethod.Get && path.StartsWith("/bridge/v1/link/", StringComparison.Ordinal) && path.EndsWith("/result", StringComparison.Ordinal))
        {
            var sessionId = path.Split('/')[4];
            if (!_links.TryGetValue(sessionId, out var link)) return Json(HttpStatusCode.NotFound, new { error = "unknown_link", message = "No such attempt." });
            if (link.Item == null) return Json(HttpStatusCode.OK, new { status = "pending", itemId = (Guid?)null });
            if (link.Reconnect != null) link.Item.Status = "active";
            return Json(HttpStatusCode.OK, new { status = "finished", itemId = link.Item.Id });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method == HttpMethod.Post && parts is ["bridge", "v1", "items", _, "pull"])
        {
            var item = Items.Single(i => i.Id == Guid.Parse(parts[3]));
            if (ThrottleAfterPulls is { } limit && item.Pulls >= limit)
            {
                var throttled = Json((HttpStatusCode)429, new { error = "rate_limited", message = "Too many pulls. Try again later." });
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return throttled;
            }
            if (item.Status != "active") return Json(HttpStatusCode.Conflict, new { error = "login_required", message = "The bank needs you to sign in again." });
            if (item.Pages.Count == 0) return new HttpResponseMessage(HttpStatusCode.NoContent);
            item.Pulls++;
            var page = item.Pages.Peek();
            var batchId = Guid.NewGuid();
            var cursorAfter = "cursor-" + (item.Pulls);
            var plain = new JsonObject
            {
                ["batch_id"] = batchId,
                ["item_id"] = item.Id,
                ["institution_name"] = item.Name,
                ["cursor_after"] = cursorAfter,
                ["has_more"] = page.HasMore,
                ["added"] = Clone(page.Added),
                ["modified"] = Clone(page.Modified),
                ["removed"] = Clone(page.Removed),
                ["accounts"] = Clone(page.Accounts),
            };
            var sealTo = ForeignKeyPoint ?? _encryptionPoint!;
            var aadServer = ForeignServerForSealing ?? ServerId;
            var sealedBytes = BridgeHpke.Seal(sealTo, BridgeHpke.FeedInfo,
                BridgeHpke.FeedAad(aadServer, batchId, item.Id, cursorAfter), Encoding.UTF8.GetBytes(plain.ToJsonString()));
            _pending[batchId] = (item, cursorAfter);
            LastBatchId = batchId;
            LastSealed = Base64Url(sealedBytes);
            return Json(HttpStatusCode.OK, new
            {
                batchId, itemId = item.Id, cursorAfter, hasMore = page.HasMore,
                added = page.Added.Count, modified = page.Modified.Count, removed = page.Removed.Count, @sealed = LastSealed
            });
        }

        if (request.Method == HttpMethod.Post && parts is ["bridge", "v1", "batches", _, "ack"])
        {
            if (FailNextAck) { FailNextAck = false; return Json(HttpStatusCode.ServiceUnavailable, new { error = "paused", message = "Paused." }); }
            var id = Guid.Parse(parts[3]);
            if (!_pending.Remove(id, out var pending)) return Json(HttpStatusCode.Conflict, new { error = "unknown_batch", message = "Already acknowledged." });
            pending.Item.Pages.Dequeue();
            pending.Item.UpdatesAvailable = pending.Item.Pages.Count > 0;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Delete && parts is ["bridge", "v1", "items", _])
        {
            if (FailRemoveWith is { } failure) return Json((HttpStatusCode)failure, new { error = "x", message = "no" });
            var item = Items.SingleOrDefault(i => i.Id == Guid.Parse(parts[3]));
            if (item == null) return Json(HttpStatusCode.NotFound, new { error = "unknown_item" });
            item.Removed = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Json(HttpStatusCode.NotFound, new { error = "no_route" });
    }

    private bool SignatureOk(HttpRequestMessage request, byte[] body)
    {
        if (_signingKey == null) return false;
        if (!long.TryParse(request.Headers.GetValues("X-Buoy-Timestamp").FirstOrDefault(), out var ts)) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300) return false;
        if (!request.Headers.Contains("X-Buoy-Server-Version")) return false;
        var canonical = $"{request.Method.Method.ToUpperInvariant()}\n{request.RequestUri!.PathAndQuery}\n{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()}\n{ts}";
        return _signingKey.VerifyData(Encoding.UTF8.GetBytes(canonical), Convert.FromBase64String(request.Headers.GetValues("X-Buoy-Signature").First()), HashAlgorithmName.SHA256);
    }

    private static JsonArray Clone(JsonArray a) => (JsonArray)JsonNode.Parse(a.ToJsonString())!;

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), new MediaTypeHeaderValue("application/json")) };
}

internal sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal static class BridgeFixtures
{
    public static JsonObject Account(string id, string name, string type = "depository", string subtype = "checking", decimal current = 1204.55m, decimal available = 1100m) =>
        new()
        {
            ["account_id"] = id, ["name"] = name, ["official_name"] = name + " Official", ["type"] = type, ["subtype"] = subtype, ["mask"] = "0000",
            ["balances"] = new JsonObject { ["current"] = current, ["available"] = available, ["iso_currency_code"] = "USD" }
        };

    public static JsonObject Tx(string id, string account, decimal plaidAmount, string name, string date = "2026-10-01", bool pending = false, string? pendingId = null) =>
        new()
        {
            ["transaction_id"] = id, ["account_id"] = account, ["amount"] = plaidAmount, ["name"] = name, ["merchant_name"] = name, ["date"] = date,
            ["pending"] = pending, ["pending_transaction_id"] = pendingId, ["iso_currency_code"] = "USD", ["payment_channel"] = "in store",
            ["personal_finance_category"] = new JsonObject { ["primary"] = "FOOD_AND_DRINK", ["detailed"] = "FOOD_AND_DRINK_COFFEE", ["confidence_level"] = "HIGH" }
        };

    public static FakePage OnePage(bool hasMore = false) => new()
    {
        Accounts = new JsonArray(Account("acc-1", "Everyday Checking"), Account("acc-card", "Rewards Card", "credit", "credit card", 312.40m, 0m), Account("acc-401k", "Retirement", "investment", "401k", 90000m, 90000m)),
        Added = new JsonArray(Tx("tx-1", "acc-1", 45.10m, "Kroger"), Tx("tx-2", "acc-1", -2500m, "Payroll Acme"), Tx("tx-3", "acc-card", 12.00m, "Coffee Shop"), Tx("tx-4", "acc-401k", -50m, "Contribution")),
        HasMore = hasMore
    };
}

/// <summary>
/// The real bridge aggregator, client, pairing service, ingest service and repositories over an in-memory database.
/// Only the Cloud (a fake that checks credentials and signatures), the categorizer and the alert service are replaced.
/// </summary>
internal sealed class BridgeHarness : IDisposable
{
    public const int UserId = 1;
    private readonly bool _ownsDb;

    public BuoyAIDbContext Db { get; }
    public FakeCloud Cloud { get; } = new();
    public CloudPairingService Pairing { get; }
    public BridgeClient Client { get; }
    public BridgeAggregator Aggregator { get; }
    public Mock<IBankConnectionAlertService> Alerts { get; } = new();
    public BridgePendingLinks Pending { get; } = new(TimeProvider.System);
    public Mock<ITransactionIngestCategorizer> Categorizer { get; } = new();
    public CloudBridgeOptions Options { get; }

    public BridgeHarness(Action<CloudBridgeOptions>? tweak = null, int? familyId = null, BuoyAIDbContext? db = null)
    {
        _ownsDb = db == null;
        Db = db ?? new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        Db.Users.Add(new User { Id = UserId, Username = "kat", Email = "kat@home.test", FirstName = "Kat", PasswordHash = "x", FamilyId = familyId });
        Db.SaveChanges();

        Categorizer.Setup(c => c.ProcessIngestedAsync(It.IsAny<IReadOnlyCollection<Transaction>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IngestCategorizationResult());

        Options = new CloudBridgeOptions { Url = "https://cloud.test" };
        tweak?.Invoke(Options);
        var optionsWrapper = Microsoft.Extensions.Options.Options.Create(Options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = "unit-test-encryption-key-0123456789" }).Build();
        var factory = new FakeFactory(Cloud);

        Pairing = new CloudPairingService(Db, new CloudSecretProtector(config), factory, optionsWrapper, NullLogger<CloudPairingService>.Instance);
        Client = new BridgeClient(factory, Pairing, optionsWrapper, TimeProvider.System, NullLogger<BridgeClient>.Instance);

        var accounts = new AccountRepository(Db);
        var transactions = new TransactionRepository(Db);
        var ingest = new TransactionIngestService(transactions, accounts, Categorizer.Object, NullLogger<TransactionIngestService>.Instance);
        Aggregator = new BridgeAggregator(Client, optionsWrapper, new PlaidConnectionRepository(Db), accounts, new UserRepository(Db),
            transactions, ingest, Alerts.Object, Pending, NullLogger<BridgeAggregator>.Instance);
    }

    public Task<CloudPairingStatus> PairAsync() => Pairing.PairAsync("ABCDE-FGHJK", "Home server");

    /// <summary>The whole connect: the user starts a page, finishes it at the bank (the fake makes <paramref name="item"/>), and comes back.</summary>
    public async Task<AggregatorConnectResult> ConnectAsync(FakeCloudItem item, int userId = UserId)
    {
        Cloud.NextLinkItem = item;
        var start = await Aggregator.StartConnectionAsync(userId, "web");
        return await Aggregator.CompleteConnectionAsync(userId, start.Value);
    }

    public FakeCloudItem AddItem(Action<FakeCloudItem>? tweak = null)
    {
        var item = new FakeCloudItem();
        tweak?.Invoke(item);
        Cloud.Items.Add(item);
        return item;
    }

    /// <summary>A Cloud bank already adopted as a connection, as a first connect would have left it.</summary>
    public async Task<PlaidConnection> SeedConnectionAsync(FakeCloudItem item, Action<PlaidConnection>? tweak = null)
    {
        var connection = new PlaidConnection
        {
            UserId = UserId, Provider = BankProvider.Bridge, AccessToken = string.Empty, ItemId = BridgeMapping.ItemId(item.Id),
            InstitutionId = "bridge", InstitutionName = item.Name, IsActive = true, Status = PlaidItemStatus.Active,
            ImportStatus = ImportStatus.Completed, LastSyncAt = DateTime.UtcNow, LastTransactionSyncAt = DateTime.UtcNow
        };
        tweak?.Invoke(connection);
        Db.PlaidConnections.Add(connection);
        await Db.SaveChangesAsync();
        return connection;
    }

    public Task<PlaidConnection> ReloadAsync(int id) => Db.PlaidConnections.AsNoTracking().SingleAsync(c => c.Id == id);

    public void Dispose()
    {
        if (_ownsDb) Db.Dispose();
    }
}
