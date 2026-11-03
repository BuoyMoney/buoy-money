using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Relayed push for self-hosted servers: the payload is encrypted to the phone on this server, and
/// the relay (a fake here; task 14 builds the real one) only ever sees ciphertext.
/// </summary>
public class PushRelayTests
{
    /// <summary>A phone: a P-256 key pair and the decryption written straight from the contract in push_relay.md.</summary>
    private sealed class Phone
    {
        private readonly ECDiffieHellman _key;

        public Phone() => _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        /// <summary>A phone with a fixed key, for the shared test vector in docs/self_host/push_relay.md.</summary>
        public Phone(string d, string x, string y)
        {
            _key = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = Convert.FromBase64String(d),
                Q = new ECPoint { X = Convert.FromBase64String(x), Y = Convert.FromBase64String(y) }
            });
        }

        public string PublicKey
        {
            get
            {
                var q = _key.ExportParameters(false).Q;
                var raw = new byte[65];
                raw[0] = 4;
                q.X!.CopyTo(raw, 1);
                q.Y!.CopyTo(raw, 33);
                return Convert.ToBase64String(raw);
            }
        }

        public JsonElement Open(string envelope)
        {
            var s = envelope.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
            bytes[0].Should().Be(1);
            var ephemeralPublic = bytes[1..66];
            var nonce = bytes[66..78];
            var ciphertext = bytes[78..^16];
            var tag = bytes[^16..];

            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = ephemeralPublic[1..33], Y = ephemeralPublic[33..65] }
            });
            var secret = _key.DeriveRawSecretAgreement(ephemeral.PublicKey);
            var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: ephemeralPublic,
                info: Encoding.ASCII.GetBytes("buoymoney-push-v1"));
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(aesKey, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, new byte[] { 1 });
            return JsonDocument.Parse(plaintext).RootElement.Clone();
        }
    }

    private sealed class FakeRelay : HttpMessageHandler
    {
        public List<(Uri Uri, JsonElement Body)> Requests { get; } = new();
        public Func<string, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.Accepted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonDocument.Parse(text).RootElement.Clone();
            Requests.Add((request.RequestUri!, body));
            return new HttpResponseMessage(Respond(body.GetProperty("pairingToken").GetString()!));
        }
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static PushRelaySender Sender(FakeRelay relay, string? url = "https://relay.test") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PushRelay:Url"] = url }).Build(),
            new FakeFactory(relay), NullLogger<PushRelaySender>.Instance);

    private static readonly IReadOnlyDictionary<string, string> Data =
        new Dictionary<string, string> { ["type"] = "budget_alert", ["route"] = "/budgets" };

    // ---- envelope ----

    [Fact]
    public void Envelope_RoundTrips_AndNothingReadableIsInTheCiphertext()
    {
        var phone = new Phone();

        var envelope = PushEnvelope.Seal(phone.PublicKey, "Dining is over budget", "You spent 412 of 400", Data);

        envelope.Should().NotContain("Dining").And.NotContain("budget");
        var plain = phone.Open(envelope);
        plain.GetProperty("title").GetString().Should().Be("Dining is over budget");
        plain.GetProperty("body").GetString().Should().Be("You spent 412 of 400");
        plain.GetProperty("data").GetProperty("route").GetString().Should().Be("/budgets");
        plain.GetProperty("ts").GetInt64().Should().BeGreaterThan(1_700_000_000);
    }

    [Fact]
    public void TheDocumentedTestVector_Opens()
    {
        var phone = new Phone("jO/CBsTnlvkQFg+2FrflxefFmt1StsH/fAWhZ6GN9eM=",
            "GCnwadxrqiERteM7e3+CIt602oFM8t4hWF/OcGBR0W8=", "hJ1LQJZc/dl/btGG1UEY4W96ze1IfbeXOPeDE3SiVNo=");
        var envelope = "AQRCEM4olUC53EDO-9e3wrTw4sMuuCZMeM0j-p0RXw41eh_MuRX_NBH8zIOEVeoOjH4uuTjJD3vLDKGDslInyha6HEE-rV_OcdMj0OmAyfJiVQUs2pOWZXa0SM_XSgmFhC5kmQTX6kcJIPe1E_3vRKj32wQOECl5f4XOkYFlUZYDMVV4fq9gFzmfeQgRFt0gzomQbCzuU5QYyz9RZCjJvP3CDlj2-ioOUeLu-qZDOvjxrHHp5NZ5i38y7P3_M7FmJCbRj_uVfEf5wIvozHeryo18FV73GvCZdKoMiGPrAA";

        var plain = phone.Open(envelope);

        plain.GetProperty("title").GetString().Should().Be("Dining is over budget");
        plain.GetProperty("ts").GetInt64().Should().Be(1760000000);
    }

    [Fact]
    public void Envelope_UsesAFreshKeyAndNonceEveryTime()
    {
        var phone = new Phone();
        PushEnvelope.Seal(phone.PublicKey, "t", "b", Data).Should().NotBe(PushEnvelope.Seal(phone.PublicKey, "t", "b", Data));
    }

    [Fact]
    public void Envelope_CannotBeOpenedByAnotherPhone_OrAfterTampering()
    {
        var phone = new Phone();
        var other = new Phone();
        var envelope = PushEnvelope.Seal(phone.PublicKey, "t", "b", Data);

        var wrongPhone = () => other.Open(envelope);
        wrongPhone.Should().Throw<CryptographicException>();

        var chars = envelope.ToCharArray();
        chars[^3] = chars[^3] == 'A' ? 'B' : 'A';
        var tampered = () => phone.Open(new string(chars));
        tampered.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Envelope_CutsALongBody_ToStayInsideThePushSizeLimit()
    {
        var phone = new Phone();
        var envelope = PushEnvelope.Seal(phone.PublicKey, "t", new string('x', 5000), Data);

        envelope.Length.Should().BeLessThan(3200);
        phone.Open(envelope).GetProperty("body").GetString()!.Length.Should().Be(200);
    }

    [Fact]
    public void Envelope_CutsAMediumBody_ToTwoHundredChars_WithoutSplittingAnEmoji()
    {
        var phone = new Phone();
        var body = new string('x', 199) + "😀" + new string('y', 300);
        var envelope = PushEnvelope.Seal(phone.PublicKey, "t", body, Data);

        var opened = phone.Open(envelope).GetProperty("body").GetString()!;
        opened.Should().Be(new string('x', 199));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void PublicKeyValidation_RejectsAnythingThatIsNotAP256Point(string? key)
    {
        PushEnvelope.IsValidPublicKey(key).Should().BeFalse();
    }

    [Fact]
    public void PublicKeyValidation_AcceptsAPhonesKey()
    {
        PushEnvelope.IsValidPublicKey(new Phone().PublicKey).Should().BeTrue();
    }

    // ---- sender ----

    [Fact]
    public async Task Sender_PostsOneEncryptedMessagePerDevice_ToTheConfiguredRelay()
    {
        var relay = new FakeRelay();
        var phone = new Phone();

        var result = await Sender(relay, "https://relay.test/").SendAsync(
            new[] { new RelayDevice("pair-1", phone.PublicKey) }, "Hello", "World", Data);

        result.SuccessCount.Should().Be(1);
        var request = relay.Requests.Single();
        request.Uri.ToString().Should().Be("https://relay.test/v1/push");
        request.Body.GetProperty("pairingToken").GetString().Should().Be("pair-1");
        var sent = request.Body.GetRawText();
        sent.Should().NotContain("Hello").And.NotContain("World", "the relay must only see ciphertext");
        phone.Open(request.Body.GetProperty("envelope").GetString()!).GetProperty("title").GetString().Should().Be("Hello");
    }

    [Fact]
    public async Task Sender_ReportsPairingsTheRelayHasForgotten_AndKeepsGoing()
    {
        var relay = new FakeRelay { Respond = token => token == "gone" ? HttpStatusCode.Gone : HttpStatusCode.Accepted };
        var phone = new Phone();

        var result = await Sender(relay).SendAsync(
            new[] { new RelayDevice("gone", phone.PublicKey), new RelayDevice("alive", phone.PublicKey) }, "t", "b", Data);

        result.InvalidTokens.Should().Equal("gone");
        result.SuccessCount.Should().Be(1);
        result.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task Sender_TreatsARelayOutageAsAFailure_NotAnException()
    {
        var relay = new FakeRelay { Respond = _ => HttpStatusCode.BadGateway };

        var result = await Sender(relay).SendAsync(new[] { new RelayDevice("p", new Phone().PublicKey) }, "t", "b", Data);

        result.SuccessCount.Should().Be(0);
        result.FailureCount.Should().Be(1);
        result.InvalidTokens.Should().BeEmpty("a 5xx must not unpair the phone");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://relay.test")]
    public async Task Sender_IsOff_WithoutAValidRelayUrl(string? url)
    {
        var relay = new FakeRelay();
        var sender = Sender(relay, url);

        sender.IsEnabled.Should().BeFalse();
        (await sender.SendAsync(new[] { new RelayDevice("p", new Phone().PublicKey) }, "t", "b", Data)).Should().Be(FcmSendResult.Empty);
        relay.Requests.Should().BeEmpty();
    }

    // ---- through the push pipeline ----

    private static (PushNotificationService Service, BuoyAIDbContext Db, Mock<IFcmSender> Fcm) Pipeline(FakeRelay relay, bool fcmEnabled)
    {
        var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var fcm = new Mock<IFcmSender>();
        fcm.Setup(f => f.IsEnabled).Returns(fcmEnabled);
        fcm.Setup(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FcmSendResult(1, 0, Array.Empty<string>()));
        return (new PushNotificationService(db, fcm.Object, NullLogger<PushNotificationService>.Instance, Sender(relay)), db, fcm);
    }

    [Fact]
    public async Task SelfHost_NoFirebase_RelayDeviceStillGetsTheNotification()
    {
        var relay = new FakeRelay();
        var phone = new Phone();
        var (service, db, fcm) = Pipeline(relay, fcmEnabled: false);
        await service.RegisterDeviceAsync(1, new RegisterDeviceRequest { Token = "pair-1", Platform = "relay", PublicKey = phone.PublicKey });

        await service.SendToUserAsync(1, "budget_alert", "Over budget", "Dining", "/budgets");

        var request = relay.Requests.Single();
        var plain = phone.Open(request.Body.GetProperty("envelope").GetString()!);
        plain.GetProperty("title").GetString().Should().Be("Over budget");
        plain.GetProperty("data").GetProperty("route").GetString().Should().Be("/budgets");
        fcm.Verify(f => f.SendAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        (await db.UserDeviceTokens.SingleAsync()).PublicKey.Should().Be(phone.PublicKey);
    }

    [Fact]
    public async Task ForgottenPairing_IsRemoved_SoItIsNotRetriedForever()
    {
        var relay = new FakeRelay { Respond = _ => HttpStatusCode.NotFound };
        var (service, db, _) = Pipeline(relay, fcmEnabled: false);
        await service.RegisterDeviceAsync(1, new RegisterDeviceRequest { Token = "pair-1", Platform = "relay", PublicKey = new Phone().PublicKey });

        await service.SendToUserAsync(1, "budget_alert", "t", "b", null);

        (await db.UserDeviceTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Hosted_FcmDevicesAreNeverSentToTheRelay_AndRelayDevicesAreNeverSentToFcm()
    {
        var relay = new FakeRelay();
        var (service, db, fcm) = Pipeline(relay, fcmEnabled: true);
        await service.RegisterDeviceAsync(1, new RegisterDeviceRequest { Token = "fcm-token", Platform = "android" });
        await service.RegisterDeviceAsync(1, new RegisterDeviceRequest { Token = "pair-1", Platform = "relay", PublicKey = new Phone().PublicKey });

        await service.SendToUserAsync(1, "budget_alert", "t", "b", null);

        fcm.Verify(f => f.SendAsync(It.Is<IReadOnlyList<string>>(t => t.Single() == "fcm-token"), "t", "b",
            It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
        relay.Requests.Single().Body.GetProperty("pairingToken").GetString().Should().Be("pair-1");
        (await db.UserDeviceTokens.FirstAsync(t => t.Platform == "android")).PublicKey.Should().BeNull();
    }

    [Fact]
    public async Task NoFirebase_NoRelayUrl_DoesNothing()
    {
        var relay = new FakeRelay();
        var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var fcm = new Mock<IFcmSender>();
        fcm.Setup(f => f.IsEnabled).Returns(false);
        var service = new PushNotificationService(db, fcm.Object, NullLogger<PushNotificationService>.Instance, Sender(relay, null));
        await service.RegisterDeviceAsync(1, new RegisterDeviceRequest { Token = "pair-1", Platform = "relay", PublicKey = new Phone().PublicKey });

        await service.SendToUserAsync(1, "budget_alert", "t", "b", null);

        relay.Requests.Should().BeEmpty();
    }
}
