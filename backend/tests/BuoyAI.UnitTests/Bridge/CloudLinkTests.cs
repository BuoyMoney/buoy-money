using System.Reflection;
using System.Security.Claims;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.Bridge;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>"Link with BuoyAI Cloud" on the self-hosted server: link/start and the anonymous link/callback.</summary>
public class CloudLinkTests
{
  private sealed class Clock : TimeProvider
  {
    private DateTimeOffset _now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
  }

  private const string Origin = "https://desktop-abc.tail1234.ts.net:10111";

  private readonly Clock _clock = new();
  private readonly CloudLinkStateStore _links;
  private readonly Mock<ICloudPairingService> _pairing = new();
  private CloudPairingStatus _status = new(true, false, "https://cloud.test", null, null, null, null);
  private string _linkUrl = "https://buoy-ai.com/app/cloud/link";

  public CloudLinkTests()
  {
    _links = new CloudLinkStateStore(new MemoryCache(new MemoryCacheOptions()), _clock);
    _pairing.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _status);
    _pairing.Setup(p => p.PairAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(new CloudPairingStatus(true, true, "https://cloud.test", Guid.NewGuid(), "Home server", DateTime.UtcNow, false));
  }

  private CloudBridgeController Controller(int userId = 7, string? baseUrl = Origin)
  {
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = baseUrl }).Build();
    var deviceLinks = new CloudDeviceLinkService(Mock.Of<IHttpClientFactory>(),
      new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new CloudBridgeOptions { Url = "https://cloud.test" }), _clock, NullLogger<CloudDeviceLinkService>.Instance);
    var controller = new CloudBridgeController(_pairing.Object, _links, deviceLinks,
      Options.Create(new CloudBridgeOptions { Url = "https://cloud.test", LinkUrl = _linkUrl }),
      config, NullLogger<CloudBridgeController>.Instance);
    var http = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, "Admin")], "test"))
    };
    http.Request.Scheme = "http";
    http.Request.Host = new HostString("192.168.1.20:8080");
    controller.ControllerContext = new ControllerContext { HttpContext = http };
    return controller;
  }

  private async Task<Dictionary<string, string>> StartAsync(string? name = "Home server")
  {
    var result = await Controller().StartLink(new CloudBridgeController.LinkStartBody(name), CancellationToken.None);
    var start = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudBridgeController.LinkStart>().Subject;
    return QueryHelpers.ParseQuery(new Uri(start.Url).Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
  }

  private static string RedirectOf(IActionResult result) => result.Should().BeOfType<RedirectResult>().Subject.Url;

  private void VerifyNoPair() =>
    _pairing.Verify(p => p.PairAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

  // shape

  [Fact]
  public void StartLink_IsAdminOnly_AndOnlyTheCallbackIsAnonymous()
  {
    typeof(CloudBridgeController).GetCustomAttribute<AuthorizeAttribute>()!.Roles.Should().Be("Admin");
    typeof(CloudBridgeController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null)
      .Select(m => m.Name).Should().Equal(nameof(CloudBridgeController.LinkCallback));
  }

  // start

  [Fact]
  public async Task StartLink_PointsAtTheLinkPage_WithStateNameAndThisServersCallback()
  {
    var result = await Controller().StartLink(new CloudBridgeController.LinkStartBody("  Home server "), CancellationToken.None);

    var url = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudBridgeController.LinkStart>().Subject.Url;
    url.Should().StartWith("https://buoy-ai.com/app/cloud/link?");
    var q = QueryHelpers.ParseQuery(new Uri(url).Query);
    q["state"].ToString().Should().HaveLength(43);
    q["name"].ToString().Should().Be("Home server");
    q["callback"].ToString().Should().Be(Origin + "/api/CloudBridge/link/callback");
  }

  [Fact]
  public async Task StartLink_GivesANewStateEachTime()
  {
    var first = await StartAsync();
    var second = await StartAsync();

    first["state"].Should().NotBe(second["state"]);
  }

  [Fact]
  public async Task StartLink_WithoutABaseUrl_UsesTheAddressTheRequestCameIn()
  {
    var controller = Controller(baseUrl: null);
    controller.HttpContext.Request.Scheme = "https";
    controller.HttpContext.Request.Host = new HostString("box.example.test");

    var result = await controller.StartLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    var url = ((CloudBridgeController.LinkStart)((OkObjectResult)result).Value!).Url;
    QueryHelpers.ParseQuery(new Uri(url).Query)["callback"].ToString()
      .Should().Be("https://box.example.test/api/CloudBridge/link/callback");
  }

  [Fact]
  public async Task StartLink_OnLocalhostOverHttp_IsAllowed()
  {
    var result = await Controller(baseUrl: "http://localhost:8080").StartLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
  }

  [Theory]
  [InlineData("http://192.168.1.20:8080")]
  [InlineData("http://umbrel.local:8421")]
  public async Task StartLink_OnAPlainHttpNetworkAddress_Is400_BeforeMakingAState(string origin)
  {
    var result = await Controller(baseUrl: origin).StartLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    var body = result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!;
    body.GetType().GetProperty("code")!.GetValue(body).Should().Be("link_needs_https");
  }

  [Fact]
  public async Task StartLink_WithoutAName_ShowsTheMachineName()
  {
    var q = await StartAsync(name: null);

    var machine = Environment.MachineName;
    q["name"].Should().Be(machine.Length > 60 ? machine[..60] : machine);
  }

  [Fact]
  public async Task StartLink_WhenTheCloudIsNotSetUp_Is400()
  {
    _status = new CloudPairingStatus(false, false, null, null, null, null, null);

    var result = await Controller().StartLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    result.Should().BeOfType<BadRequestObjectResult>();
  }

  [Fact]
  public async Task StartLink_WhenAlreadyLinked_Is409()
  {
    _status = new CloudPairingStatus(true, true, "https://cloud.test", Guid.NewGuid(), "Home", DateTime.UtcNow, null);

    var result = await Controller().StartLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    result.Should().BeOfType<ConflictObjectResult>();
  }

  [Fact]
  public async Task StartLink_ANameOver60Characters_Is400()
  {
    var result = await Controller().StartLink(new CloudBridgeController.LinkStartBody(new string('x', 61)), CancellationToken.None);

    result.Should().BeOfType<BadRequestObjectResult>();
  }

  [Theory]
  [InlineData("http://buoy-ai.com/app/cloud/link")]
  [InlineData("javascript:alert(1)")]
  [InlineData("not a url")]
  public async Task StartLink_ALinkUrlThatIsNotHttps_Is400(string linkUrl)
  {
    _linkUrl = linkUrl;

    var result = await Controller().StartLink(new CloudBridgeController.LinkStartBody(null), CancellationToken.None);

    result.Should().BeOfType<BadRequestObjectResult>();
  }

  // callback

  [Fact]
  public async Task Callback_WithTheRightState_PairsWithTheCodeAndTheStoredName_ThenSaysLinked()
  {
    var q = await StartAsync("Home server");

    var result = await Controller().LinkCallback("ABCDE-FGHJK", q["state"], null, CancellationToken.None);

    RedirectOf(result).Should().Be(Origin + "/app/settings/cloud?linked=1");
    _pairing.Verify(p => p.PairAsync("ABCDE-FGHJK", "Home server", It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task Callback_AStateWorksOnce()
  {
    var q = await StartAsync();
    await Controller().LinkCallback("ABCDE-FGHJK", q["state"], null, CancellationToken.None);

    var again = await Controller().LinkCallback("ZZZZZ-ZZZZZ", q["state"], null, CancellationToken.None);

    RedirectOf(again).Should().Be(Origin + "/app/settings/cloud?link_error=expired");
    _pairing.Verify(p => p.PairAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task Callback_AfterFifteenMinutes_TheStateHasExpired()
  {
    var q = await StartAsync();
    _clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

    var result = await Controller().LinkCallback("ABCDE-FGHJK", q["state"], null, CancellationToken.None);

    RedirectOf(result).Should().EndWith("?link_error=expired");
    VerifyNoPair();
  }

  [Fact]
  public async Task Callback_JustBeforeFifteenMinutes_StillWorks()
  {
    var q = await StartAsync();
    _clock.Advance(TimeSpan.FromMinutes(14));

    var result = await Controller().LinkCallback("ABCDE-FGHJK", q["state"], null, CancellationToken.None);

    RedirectOf(result).Should().EndWith("?linked=1");
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("not-the-state")]
  public async Task Callback_AWrongOrMissingState_PairsNothing(string? state)
  {
    await StartAsync();

    var result = await Controller().LinkCallback("ABCDE-FGHJK", state, null, CancellationToken.None);

    RedirectOf(result).Should().EndWith("?link_error=expired");
    VerifyNoPair();
  }

  [Fact]
  public async Task Callback_WhenTheOwnerCancelled_SaysCancelled_AndSpendsTheState()
  {
    var q = await StartAsync();

    var result = await Controller().LinkCallback(null, q["state"], "access_denied", CancellationToken.None);
    var later = await Controller().LinkCallback("ABCDE-FGHJK", q["state"], null, CancellationToken.None);

    RedirectOf(result).Should().Be(Origin + "/app/settings/cloud?link_error=cancelled");
    RedirectOf(later).Should().EndWith("?link_error=expired");
    VerifyNoPair();
  }

  [Fact]
  public async Task Callback_AnyOtherError_SaysFailed_WithoutEchoingIt()
  {
    var q = await StartAsync();

    var result = await Controller().LinkCallback(null, q["state"], "<script>x</script>", CancellationToken.None);

    RedirectOf(result).Should().Be(Origin + "/app/settings/cloud?link_error=failed");
    VerifyNoPair();
  }

  [Fact]
  public async Task Callback_WithoutACode_SaysFailed()
  {
    var q = await StartAsync();

    var result = await Controller().LinkCallback(" ", q["state"], null, CancellationToken.None);

    RedirectOf(result).Should().EndWith("?link_error=failed");
    VerifyNoPair();
  }

  [Fact]
  public async Task Callback_WhenTheCloudRefusesTheCode_SaysFailed_WithoutTheCloudsMessage()
  {
    var q = await StartAsync();
    _pairing.Setup(p => p.PairAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new DomainValidationException("That pairing code is wrong, used or expired."));

    var result = await Controller().LinkCallback("ABCDE-FGHJK", q["state"], null, CancellationToken.None);

    RedirectOf(result).Should().Be(Origin + "/app/settings/cloud?link_error=failed");
  }

  [Fact]
  public async Task Callback_IsNeverCached()
  {
    var q = await StartAsync();
    var controller = Controller();

    await controller.LinkCallback("ABCDE-FGHJK", q["state"], null, CancellationToken.None);

    controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
  }

  // the state store

  [Fact]
  public void StateStore_KeepsOnlyAHashOfTheState()
  {
    var cache = new MemoryCache(new MemoryCacheOptions());
    var store = new CloudLinkStateStore(cache, _clock);

    var state = store.Create(7, "Home");

    cache.TryGetValue("cloud-link-state:" + state, out _).Should().BeFalse();
    cache.Count.Should().Be(1);
    store.Consume(state).Should().Be(new CloudLinkState(7, "Home"));
  }

  [Fact]
  public void StateStore_RefusesAnOverlongState() =>
    _links.Consume(new string('a', 201)).Should().BeNull();
}
