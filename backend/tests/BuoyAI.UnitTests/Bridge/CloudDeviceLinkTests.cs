using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BuoyAI.API.Controllers;
using BuoyAI.API.Filters;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.Bridge;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>"Link with BuoyAI Cloud" by code on the self-hosted server: link/device/start, status, cancel, and the poll loop.</summary>
public class CloudDeviceLinkTests : IDisposable
{
  private sealed class Clock : TimeProvider
  {
    private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
  }

  /// <summary>The Cloud's pairing API as far as the code link uses it: start a session, then answer polls in order.</summary>
  private sealed class FakeCloud : HttpMessageHandler
  {
    public Func<HttpResponseMessage> Start { get; set; } = () => Json(HttpStatusCode.OK, new
    {
      deviceCode = DeviceCode,
      userCode = "BCDF-GHJK",
      expiresIn = 600,
      interval = 5,
      verificationUri = "https://cloud.buoymoney.com/link",
      verificationUriComplete = "https://cloud.buoymoney.com/link?code=BCDF-GHJK"
    });

    public Queue<Func<HttpResponseMessage>> Polls { get; } = new();
    public List<string> StartBodies { get; } = new();
    public List<string> PollBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
      var path = request.RequestUri!.AbsolutePath;
      if (path.EndsWith("/v1/pairing/link-sessions/poll"))
      {
        PollBodies.Add(body);
        return Polls.Count > 0 ? Polls.Dequeue()() : Json(HttpStatusCode.OK, new { status = "pending" });
      }
      if (path.EndsWith("/v1/pairing/link-sessions"))
      {
        StartBodies.Add(body);
        return Start();
      }
      return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
      new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
  }

  private const string DeviceCode = "dev1ce-c0de-only-the-server-holds-AAAAAAAAAA";
  private const string Origin = "https://desktop-abc.tail1234.ts.net:10111";

  private readonly Clock _clock = new();
  private readonly FakeCloud _cloud = new();
  private readonly Mock<ICloudPairingService> _pairing = new();
  private readonly CloudDeviceLinkService _service;
  private readonly CloudLinkStateStore _linkStates;
  private readonly List<TimeSpan> _waits = new();
  private CloudPairingStatus _status = new(true, false, "https://cloud.test", null, null, null, null);

  public CloudDeviceLinkTests()
  {
    _linkStates = new CloudLinkStateStore(new MemoryCache(new MemoryCacheOptions()), _clock);
    _pairing.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _status);
    _pairing.Setup(p => p.PairAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new CloudPairingStatus(true, true, "https://cloud.test", Guid.NewGuid(), "Home server", DateTime.UtcNow, false));

    var http = new Mock<IHttpClientFactory>();
    http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(_cloud, disposeHandler: false));
    var scopes = new ServiceCollection().AddSingleton(_pairing.Object).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    _service = new CloudDeviceLinkService(http.Object, scopes, Options.Create(new CloudBridgeOptions { Url = "https://cloud.test" }),
      _clock, NullLogger<CloudDeviceLinkService>.Instance)
    {
      // Each wait moves the clock on by the wait and returns at once.
      Delay = (wait, ct) =>
      {
        ct.ThrowIfCancellationRequested();
        _waits.Add(wait);
        _clock.Advance(wait);
        return Task.CompletedTask;
      }
    };
  }

  public void Dispose() => _service.Dispose();

  private CloudBridgeController Controller(string? baseUrl = Origin)
  {
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = baseUrl }).Build();
    var controller = new CloudBridgeController(_pairing.Object,
      _linkStates, _service,
      Options.Create(new CloudBridgeOptions { Url = "https://cloud.test" }), config, NullLogger<CloudBridgeController>.Instance);
    var http = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(ClaimTypes.Role, "Admin")], "test"))
    };
    controller.ControllerContext = new ControllerContext { HttpContext = http };
    return controller;
  }

  private static HttpResponseMessage Poll(string status, string? code = null) =>
    FakeCloud.Json(HttpStatusCode.OK, code == null ? new { status } : new { status, code, expiresAt = DateTime.UtcNow.AddMinutes(10) });

  private static HttpResponseMessage Error(HttpStatusCode status, string error) => FakeCloud.Json(status, new { error });

  private async Task<DeviceLinkStart> StartAsync(string? name = "Home server")
  {
    var result = await Controller().StartDeviceLink(new CloudBridgeController.LinkStartBody(name), CancellationToken.None);
    return result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DeviceLinkStart>().Subject;
  }

  private void VerifyNoPair() =>
    _pairing.Verify(p => p.PairAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

  // ---- shape

  [Fact]
  public void DeviceLinkEndpoints_AreAdminOnly_SelfHostOnly_AndNeverAnonymous()
  {
    typeof(CloudBridgeController).GetCustomAttribute<AuthorizeAttribute>()!.Roles.Should().Be("Admin");
    typeof(CloudBridgeController).GetCustomAttribute<SelfHostOnlyAttribute>().Should().NotBeNull();
    foreach (var name in new[] { nameof(CloudBridgeController.StartDeviceLink), nameof(CloudBridgeController.GetDeviceLinkStatus),
               nameof(CloudBridgeController.CancelDeviceLink) })
      typeof(CloudBridgeController).GetMethod(name)!.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
  }

  // ---- start

  [Fact]
  public async Task StartDeviceLink_ReturnsTheCodeAndTheApprovePage_ButNeverTheDeviceCode()
  {
    var startedAt = _clock.GetUtcNow().UtcDateTime;
    var start = await StartAsync();

    start.UserCode.Should().Be("BCDF-GHJK");
    start.VerificationUri.Should().Be("https://cloud.buoymoney.com/link");
    start.VerificationUriComplete.Should().Be("https://cloud.buoymoney.com/link?code=BCDF-GHJK");
    start.Interval.Should().Be(5);
    start.ExpiresAt.Should().Be(startedAt.AddMinutes(10));
    JsonSerializer.Serialize(start).Should().NotContain(DeviceCode);
    var sent = JsonNode.Parse(_cloud.StartBodies.Single())!;
    sent["name"]!.GetValue<string>().Should().Be("Home server");
    sent["serverVersion"]!.GetValue<string>().Should().NotBeNullOrEmpty();
  }

  [Fact]
  public async Task StartDeviceLink_WorksOnAPlainHttpServerAddress()
  {
    var result = await Controller(baseUrl: "http://192.168.1.20:8080").StartDeviceLink(new CloudBridgeController.LinkStartBody(null),
      CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
  }

  [Fact]
  public async Task StartDeviceLink_WithoutAName_SendsTheMachineName()
  {
    await StartAsync(name: null);

    var machine = Environment.MachineName;
    JsonNode.Parse(_cloud.StartBodies.Single())!["name"]!.GetValue<string>().Should().Be(machine.Length > 60 ? machine[..60] : machine);
  }

  [Fact]
  public async Task StartDeviceLink_WhenTheCloudIsNotSetUp_Is400()
  {
    _status = new CloudPairingStatus(false, false, null, null, null, null, null);

    var result = await Controller().StartDeviceLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    result.Should().BeOfType<BadRequestObjectResult>();
    _cloud.StartBodies.Should().BeEmpty();
  }

  [Fact]
  public async Task StartDeviceLink_WhenAlreadyLinked_Is409()
  {
    _status = new CloudPairingStatus(true, true, "https://cloud.test", Guid.NewGuid(), "Home", DateTime.UtcNow, null);

    var result = await Controller().StartDeviceLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    result.Should().BeOfType<ConflictObjectResult>();
  }

  [Fact]
  public async Task StartDeviceLink_ANameOver60Characters_Is400()
  {
    var result = await Controller().StartDeviceLink(new CloudBridgeController.LinkStartBody(new string('x', 61)), CancellationToken.None);

    result.Should().BeOfType<BadRequestObjectResult>();
  }

  [Fact]
  public async Task StartDeviceLink_WhenTheCloudSaysTooMany_SaysSoPlainly()
  {
    _cloud.Start = () => Error(HttpStatusCode.TooManyRequests, "rate_limited");

    var act = () => _service.StartAsync(7, "Home");

    (await act.Should().ThrowAsync<DomainValidationException>()).Which.Message.Should().Contain("Too many");
    _service.GetStatus().State.Should().Be("none");
  }

  [Fact]
  public async Task StartDeviceLink_AnApprovePageThatIsNotHttps_IsRefused()
  {
    _cloud.Start = () => FakeCloud.Json(HttpStatusCode.OK, new
    {
      deviceCode = DeviceCode, userCode = "BCDF-GHJK", expiresIn = 600, interval = 5,
      verificationUri = "javascript:alert(1)", verificationUriComplete = "javascript:alert(1)"
    });

    var act = () => _service.StartAsync(7, "Home");

    await act.Should().ThrowAsync<DomainValidationException>();
    _service.GetStatus().State.Should().Be("none");
  }

  // ---- status

  [Fact]
  public void Status_BeforeAnyStart_IsNone()
  {
    var result = Controller().GetDeviceLinkStatus();

    result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().Be(new DeviceLinkStatus("none"));
  }

  [Fact]
  public async Task Status_WhileWaiting_ShowsTheCodeAndExpiry()
  {
    var gate = new TaskCompletionSource();
    _service.Delay = (_, ct) => gate.Task.WaitAsync(ct);
    var start = await StartAsync();

    var status = (DeviceLinkStatus)((OkObjectResult)Controller().GetDeviceLinkStatus()).Value!;

    status.State.Should().Be("waiting");
    status.UserCode.Should().Be("BCDF-GHJK");
    status.ExpiresAt.Should().Be(start.ExpiresAt);
    status.VerificationUriComplete.Should().Be(start.VerificationUriComplete);
  }

  [Fact]
  public async Task Status_PastTheExpiry_SaysExpired_EvenBeforeTheLoopWakes()
  {
    var gate = new TaskCompletionSource();
    _service.Delay = (_, ct) => gate.Task.WaitAsync(ct);
    await StartAsync();

    _clock.Advance(TimeSpan.FromMinutes(10));

    _service.GetStatus().State.Should().Be("expired");
  }

  // ---- the background poll

  [Fact]
  public async Task BackgroundPoll_PairsWithTheCloudsCodeOnApproval()
  {
    _cloud.Polls.Enqueue(() => Poll("pending"));
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));

    await StartAsync("Home server");
    await _service.PollLoop;

    _pairing.Verify(p => p.PairAsync("ABCDE-FGHJK", "Home server", It.IsAny<CancellationToken>()), Times.Once);
    _service.GetStatus().State.Should().Be("linked");
    _cloud.PollBodies.Should().HaveCount(2).And.AllSatisfy(b => JsonNode.Parse(b)!["deviceCode"]!.GetValue<string>().Should().Be(DeviceCode));
    _waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task BackgroundPoll_OnSlowDown_WaitsFiveSecondsLonger()
  {
    _cloud.Polls.Enqueue(() => Error(HttpStatusCode.TooManyRequests, "slow_down"));
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));

    await StartAsync();
    await _service.PollLoop;

    _waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    _service.GetStatus().State.Should().Be("linked");
  }

  [Fact]
  public async Task BackgroundPoll_WhenTheOwnerDenies_SaysDenied_AndPairsNothing()
  {
    _cloud.Polls.Enqueue(() => Error(HttpStatusCode.Gone, "denied"));

    await StartAsync();
    await _service.PollLoop;

    _service.GetStatus().State.Should().Be("denied");
    VerifyNoPair();
  }

  [Fact]
  public async Task BackgroundPoll_WhenTheCloudSaysExpired_SaysExpired()
  {
    _cloud.Polls.Enqueue(() => Error(HttpStatusCode.Gone, "expired"));

    await StartAsync();
    await _service.PollLoop;

    _service.GetStatus().State.Should().Be("expired");
    VerifyNoPair();
  }

  [Fact]
  public async Task BackgroundPoll_StopsByItselfAtTheExpiry()
  {
    _cloud.Start = () => FakeCloud.Json(HttpStatusCode.OK, new
    {
      deviceCode = DeviceCode, userCode = "BCDF-GHJK", expiresIn = 12, interval = 5,
      verificationUri = "https://cloud.buoymoney.com/link", verificationUriComplete = "https://cloud.buoymoney.com/link?code=BCDF-GHJK"
    });

    await StartAsync();
    await _service.PollLoop;

    _cloud.PollBodies.Should().HaveCount(2);
    _service.GetStatus().State.Should().Be("expired");
  }

  [Fact]
  public async Task BackgroundPoll_KeepsGoingThroughANetworkBlipOrACloudHiccup()
  {
    _cloud.Polls.Enqueue(() => throw new HttpRequestException("offline"));
    _cloud.Polls.Enqueue(() => new HttpResponseMessage(HttpStatusCode.BadGateway));
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));

    await StartAsync();
    await _service.PollLoop;

    _service.GetStatus().State.Should().Be("linked");
  }

  [Fact]
  public async Task BackgroundPoll_WhenPairingFails_SaysFailedWithTheReason()
  {
    _pairing.Setup(p => p.PairAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new DomainValidationException("That pairing code is wrong, used or expired."));
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));

    await StartAsync();
    await _service.PollLoop;

    var status = _service.GetStatus();
    status.State.Should().Be("failed");
    status.Message.Should().Be("That pairing code is wrong, used or expired.");
  }

  [Fact]
  public async Task BackgroundPoll_ACodeAlreadyUsed_SaysFailed()
  {
    _cloud.Polls.Enqueue(() => Poll("used"));

    await StartAsync();
    await _service.PollLoop;

    _service.GetStatus().State.Should().Be("failed");
    VerifyNoPair();
  }

  // ---- cancel

  [Fact]
  public async Task Cancel_StopsPolling_AndForgetsTheSession()
  {
    var gate = new TaskCompletionSource();
    _service.Delay = (_, ct) => gate.Task.WaitAsync(ct);
    await StartAsync();
    var loop = _service.PollLoop;

    var result = Controller().CancelDeviceLink();
    gate.SetResult();
    await loop;

    result.Should().BeOfType<NoContentResult>();
    _cloud.PollBodies.Should().BeEmpty();
    _service.GetStatus().Should().Be(new DeviceLinkStatus("none"));
    VerifyNoPair();
  }

  [Fact]
  public async Task Start_AgainReplacesTheOldSession()
  {
    var gate = new TaskCompletionSource();
    _service.Delay = (_, ct) => gate.Task.WaitAsync(ct);
    await StartAsync();
    var first = _service.PollLoop;

    _cloud.Start = () => FakeCloud.Json(HttpStatusCode.OK, new
    {
      deviceCode = "second", userCode = "LMNP-QRST", expiresIn = 600, interval = 5,
      verificationUri = "https://cloud.buoymoney.com/link", verificationUriComplete = "https://cloud.buoymoney.com/link?code=LMNP-QRST"
    });
    await StartAsync();
    await first;

    first.IsCompleted.Should().BeTrue();
    _service.GetStatus().UserCode.Should().Be("LMNP-QRST");
  }

  [Fact]
  public async Task Unpair_AlsoClearsAFinishedCodeLink()
  {
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));
    await StartAsync();
    await _service.PollLoop;

    await Controller().Unpair(CancellationToken.None);

    _service.GetStatus().State.Should().Be("none");
  }

  // ---- already linked another way

  [Fact]
  public async Task BackgroundPoll_WhenTheServerGotLinkedAnotherWay_DoesNotPairAgain()
  {
    _cloud.Polls.Enqueue(() => Poll("pending"));
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));
    var gate = new TaskCompletionSource();
    var calls = 0;
    _service.Delay = (_, ct) => calls++ == 0 ? Task.CompletedTask : gate.Task.WaitAsync(ct);
    await StartAsync();
    // Linked by a pasted code while the first wait ran; the approval arrives afterwards.
    _status = _status with { Paired = true };
    gate.SetResult();
    await _service.PollLoop;

    VerifyNoPair();
    _service.GetStatus().State.Should().Be("linked");
  }

  [Fact]
  public async Task Pair_ByPastedCode_StopsAWaitingCodeLink()
  {
    var gate = new TaskCompletionSource();
    _service.Delay = (_, ct) => gate.Task.WaitAsync(ct);
    await StartAsync();
    var loop = _service.PollLoop;

    await Controller().Pair(new CloudBridgeController.PairBody("ABCDE-FGHJK", "Home server"), CancellationToken.None);
    gate.SetResult();
    await loop;

    _service.GetStatus().State.Should().Be("none");
    _pairing.Verify(p => p.PairAsync("ABCDE-FGHJK", "Home server", It.IsAny<CancellationToken>()), Times.Once);
    _cloud.PollBodies.Should().BeEmpty();
  }

  [Fact]
  public async Task Callback_AfterTheBrowserLink_StopsAWaitingCodeLink()
  {
    var gate = new TaskCompletionSource();
    _service.Delay = (_, ct) => gate.Task.WaitAsync(ct);
    await StartAsync();
    var loop = _service.PollLoop;
    var state = _linkStates.Create(7, "Home server");

    await Controller().LinkCallback("ABCDE-FGHJK", state, null, CancellationToken.None);
    gate.SetResult();
    await loop;

    _service.GetStatus().State.Should().Be("none");
    _cloud.PollBodies.Should().BeEmpty();
  }
  // ---- one pairing change at a time

  [Fact]
  public async Task BackgroundPoll_AnApprovalWhileAPastedCodeIsPairing_WaitsAndThenDoesNotPairOverIt()
  {
    var manualGate = new TaskCompletionSource<CloudPairingStatus>();
    _pairing.Setup(p => p.PairAsync("MANUAL-CODE", It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(manualGate.Task);
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));
    var delayGate = new TaskCompletionSource();
    _service.Delay = (_, ct) => delayGate.Task.WaitAsync(ct);
    await StartAsync();
    var loop = _service.PollLoop;

    var manual = Controller().Pair(new CloudBridgeController.PairBody("MANUAL-CODE", "Home server"), CancellationToken.None);
    delayGate.SetResult();
    await WaitUntil(() => _cloud.PollBodies.Count == 1);
    await Task.Delay(50);
    _pairing.Verify(p => p.PairAsync("ABCDE-FGHJK", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    manualGate.SetResult(new CloudPairingStatus(true, true, "https://cloud.test", Guid.NewGuid(), "Home server", DateTime.UtcNow, false));
    await manual;
    await loop;

    _pairing.Verify(p => p.PairAsync("ABCDE-FGHJK", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    _service.GetStatus().State.Should().Be("none");
  }

  [Fact]
  public async Task Unpair_WhileTheCodeLinkIsStoringItsCredential_WaitsAndThenWins()
  {
    var calls = new List<string>();
    var pairGate = new TaskCompletionSource<CloudPairingStatus>();
    _pairing.Setup(p => p.PairAsync("ABCDE-FGHJK", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .Returns(() =>
      {
        calls.Add("pair");
        return pairGate.Task;
      });
    _pairing.Setup(p => p.UnpairAsync(It.IsAny<CancellationToken>())).Returns(() =>
    {
      calls.Add("unpair");
      return Task.CompletedTask;
    });
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));
    await StartAsync();
    await WaitUntil(() => calls.Contains("pair"));

    var unpair = Controller().Unpair(CancellationToken.None);
    await Task.Delay(50);
    calls.Should().Equal("pair");

    pairGate.SetResult(new CloudPairingStatus(true, true, "https://cloud.test", Guid.NewGuid(), "Home server", DateTime.UtcNow, false));
    await unpair;

    calls.Should().Equal("pair", "unpair");
    _service.GetStatus().State.Should().Be("none");
  }

  [Fact]
  public async Task Status_AnApprovedPairingThatCrossesTheExpiry_StaysWaitingAndThenSaysLinked()
  {
    var pairGate = new TaskCompletionSource<CloudPairingStatus>();
    var pairing = false;
    _pairing.Setup(p => p.PairAsync("ABCDE-FGHJK", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .Returns(() =>
      {
        pairing = true;
        return pairGate.Task;
      });
    _cloud.Polls.Enqueue(() => Poll("approved", "ABCDE-FGHJK"));
    await StartAsync();
    await WaitUntil(() => pairing);

    _clock.Advance(TimeSpan.FromMinutes(20));
    _service.GetStatus().State.Should().Be("waiting");

    pairGate.SetResult(new CloudPairingStatus(true, true, "https://cloud.test", Guid.NewGuid(), "Home server", DateTime.UtcNow, false));
    await _service.PollLoop;
    _service.GetStatus().State.Should().Be("linked");
  }

  private static async Task WaitUntil(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition())
    {
      if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition never came true.");
      await Task.Delay(10);
    }
  }
}
