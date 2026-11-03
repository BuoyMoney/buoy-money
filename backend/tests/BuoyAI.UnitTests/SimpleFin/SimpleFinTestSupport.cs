using System.Net;
using System.Text;
using BuoyAI.Core.Configuration;
using BuoyAI.Infrastructure.Services.SimpleFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BuoyAI.UnitTests.SimpleFin;

/// <summary>
/// Recorded-style SimpleFIN replies, written from the public protocol description at
/// https://www.simplefin.org/protocol.html. The real SimpleFIN service is never called in tests.
/// </summary>
internal static class SimpleFinFixtures
{
    public const string ClaimUrl = "https://bridge.simplefin.org/simplefin/claim/DEMO-TOKEN-1";
    public const string AccessUrl = "https://demo-user:demo-pass@bridge.simplefin.org/simplefin";

    public static string SetupToken(string url = ClaimUrl) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url));

    // Unix seconds used below: 1760000000 = 2025-10-09 08:53:20 UTC.
    public const long Oct9 = 1760000000;

    /// <summary>The protocol's own example shape: one bank, a checking account with two posted charges and one pending.</summary>
    public const string OneBank = """
    {
      "errors": [],
      "accounts": [
        {
          "org": { "domain": "mybank.example", "sfin-url": "https://sfin.mybank.example", "name": "My Bank", "id": "mybank" },
          "id": "ACT-checking-1",
          "name": "Everyday Checking",
          "currency": "USD",
          "balance": "1204.55",
          "available-balance": "1100.00",
          "balance-date": 1760000000,
          "transactions": [
            { "id": "TRN-1", "posted": 1759900000, "amount": "-45.10", "description": "KROGER #123 WICHITA KS" },
            { "id": "TRN-2", "posted": 1759950000, "amount": "2500.00", "description": "PAYROLL ACME", "transacted_at": 1759940000 },
            { "id": "TRN-3", "posted": 0, "amount": "-9.99", "description": "PENDING STREAMING", "pending": true }
          ],
          "extra": {}
        }
      ]
    }
    """;

    /// <summary>Two banks: a checking account, a credit card (negative balance means money owed) and a retirement account.</summary>
    public const string ThreeAccounts = """
    {
      "errors": [],
      "accounts": [
        {
          "org": { "domain": "mybank.example", "name": "My Bank", "id": "mybank" },
          "id": "ACT-checking-1", "name": "Everyday Checking", "currency": "USD",
          "balance": "1204.55", "available-balance": "1100.00", "balance-date": 1760000000,
          "transactions": [ { "id": "TRN-1", "posted": 1759900000, "amount": "-45.10", "description": "KROGER #123" } ]
        },
        {
          "org": { "domain": "cardco.example", "name": "Card Co", "id": "cardco" },
          "id": "ACT-card-1", "name": "Visa Rewards Credit Card", "currency": "USD",
          "balance": "-310.20", "available-balance": "4689.80", "balance-date": 1760000000,
          "transactions": [ { "id": "TRN-9", "posted": 1759800000, "amount": "-20.00", "description": "COFFEE SHOP" } ]
        },
        {
          "org": { "domain": "mybank.example", "name": "My Bank", "id": "mybank" },
          "id": "ACT-ira-1", "name": "Traditional IRA", "currency": "USD",
          "balance": "50000.00", "balance-date": 1760000000,
          "transactions": [ { "id": "TRN-77", "posted": 1759800000, "amount": "100.00", "description": "CONTRIBUTION" } ]
        }
      ]
    }
    """;

    /// <summary>Version 1 reports problems as plain strings.</summary>
    public const string StringErrors = """
    {
      "errors": [ "Please log in again at My Bank (https://evil.example/phish) to continue." ],
      "accounts": [
        { "org": { "name": "My Bank" }, "id": "ACT-checking-1", "name": "Everyday Checking", "currency": "USD",
          "balance": "10.00", "transactions": [ { "id": "TRN-1", "posted": 1759900000, "amount": "-1.00", "description": "X" } ] }
      ]
    }
    """;

    /// <summary>The newer shape reports coded errors.</summary>
    public const string ErrList = """
    {
      "errlist": [ { "code": "con.auth", "msg": "Authentication failed.\nSign in at the bank again.", "conn_id": "c1" } ],
      "accounts": []
    }
    """;

    public const string Empty = """{ "errors": [], "accounts": [] }""";
}

/// <summary>A stand-in for the SimpleFIN bridge. Records every request and answers from a function.</summary>
internal sealed class FakeBridgeHandler : HttpMessageHandler
{
    public sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, long? ContentLength);

    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public List<Seen> Requests { get; } = new();

    public FakeBridgeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public static FakeBridgeHandler Always(HttpStatusCode status, string body = "") =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new Seen(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentLength));
        return Task.FromResult(_respond(request));
    }
}

internal static class SimpleFinTestFactory
{
    public static SimpleFinOptions Options(Action<SimpleFinOptions>? tweak = null)
    {
        var options = new SimpleFinOptions();
        tweak?.Invoke(options);
        return options;
    }

    public static SimpleFinClient Client(HttpMessageHandler handler, SimpleFinOptions? options = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));
        return new SimpleFinClient(
            factory.Object,
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            NullLogger<SimpleFinClient>.Instance);
    }
}
