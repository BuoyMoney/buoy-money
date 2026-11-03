using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Configuration;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.CloudPortal;
using BuoyAI.UnitTests.Services;

namespace BuoyAI.UnitTests.Controllers;

public class CloudServersControllerTests
{
  private sealed class StubHandler : HttpMessageHandler
  {
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> Bodies { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);
    public Exception? Throw { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Requests.Add(request);
      Bodies.Add(request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult());
      if (Throw != null) throw Throw;
      return Task.FromResult(Respond(request));
    }
  }

  private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
  }

  private const string ValidOpToken = "op-token-ok";

  private readonly StubHandler _handler = new();
  private readonly Mock<IUserRepository> _users = new();
  private readonly Mock<IStepUpAuthService> _stepUp = new();
  private readonly Mock<IEntitlementService> _entitlements = new();

  private User _user = new() { Id = 42, StripeCustomerId = "cus_42", HasMfaEnabled = true, IsEmailVerified = true };

  public CloudServersControllerTests()
  {
    _users.Setup(u => u.GetByIdAsync(42)).ReturnsAsync(() => _user);
    _stepUp.Setup(s => s.ValidateOperationTokenAsync(ValidOpToken, ProtectedOperation.ManageCloudServers, 42)).ReturnsAsync(true);
    _stepUp.Setup(s => s.GetRequirementLevel(It.IsAny<ProtectedOperation>())).Returns(MfaRequirementLevel.Administrative);
  }

  private CloudServersController Controller(string? opToken = null, string key = CloudOwnerTokenIssuerTests.Key)
  {
    var options = Options.Create(new CloudPortalOptions { PairingUrl = "http://127.0.0.1:5201", OwnerJwtKey = key });
    var portal = new CloudPairingPortalClient(new Factory(_handler), options, new CloudOwnerTokenIssuer(options),
      NullLogger<CloudPairingPortalClient>.Instance);
    var controller = new CloudServersController(portal, _users.Object, _stepUp.Object, _entitlements.Object);
    var http = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "42")], "test"))
    };
    if (opToken != null) http.Request.Headers["X-Operation-Token"] = opToken;
    controller.ControllerContext = new ControllerContext { HttpContext = http };
    return controller;
  }

  private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
    new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

  private JwtSecurityToken SentToken()
  {
    var auth = _handler.Requests.Single().Headers.Authorization!;
    auth.Scheme.Should().Be("Bearer");
    return new JwtSecurityTokenHandler().ReadJwtToken(auth.Parameter);
  }

  private static (int Status, string? Code) Problem(IActionResult result)
  {
    var obj = result.Should().BeAssignableTo<ObjectResult>().Subject;
    var code = obj.Value?.GetType().GetProperty("code")?.GetValue(obj.Value) as string;
    var error = obj.Value?.GetType().GetProperty("error")?.GetValue(obj.Value) as string;
    return (obj.StatusCode ?? 0, code ?? error);
  }

  private const string ServersJson = """
    [
      {"id":"11111111-1111-1111-1111-111111111111","name":"Old box","serverVersion":"1.5.30","createdAt":"2026-09-01T10:00:00Z",
       "confirmedAt":"2026-09-01T10:05:00Z","lastSeenAt":"2026-09-02T10:00:00Z","revokedAt":"2026-10-01T00:00:00Z","deliveryResumesAt":null},
      {"id":"22222222-2222-2222-2222-222222222222","name":"Home server","serverVersion":"1.5.33","createdAt":"2026-10-30T10:00:00Z",
       "confirmedAt":"2026-10-30T10:05:00Z","lastSeenAt":"2026-10-31T10:00:00Z","revokedAt":null,"deliveryResumesAt":null},
      {"id":"33333333-3333-3333-3333-333333333333","name":"New box","serverVersion":null,"createdAt":"2026-10-31T11:00:00Z",
       "confirmedAt":null,"lastSeenAt":null,"revokedAt":null,"deliveryResumesAt":null}
    ]
    """;

  // ── shape ──

  [Fact]
  public void TheController_RequiresSignIn_IsHostedOnly_AndInheritsBaseController()
  {
    typeof(CloudServersController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
    typeof(CloudServersController).GetCustomAttribute<BuoyAI.API.Filters.HostedOnlyAttribute>().Should().NotBeNull();
    typeof(CloudServersController).BaseType.Should().Be(typeof(BaseController));
    typeof(CloudServersController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .Should().NotContain(m => m.GetCustomAttribute<AllowAnonymousAttribute>() != null);
  }

  // ── list ──

  [Fact]
  public async Task List_ReturnsTheServersNewestFirst_WithAStatusEach()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, ServersJson);

    var result = await Controller().List(CancellationToken.None);

    var servers = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<IReadOnlyList<CloudServer>>().Subject;
    servers.Select(s => s.Name).Should().Equal("New box", "Home server", "Old box");
    servers.Select(s => s.Status).Should().Equal("awaiting_confirmation", "linked", "revoked");
    _handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
    _handler.Requests.Single().RequestUri!.ToString().Should().Be("http://127.0.0.1:5201/v1/pairing/servers");
  }

  [Fact]
  public async Task List_SignsAsTheUsersStripeCustomer_WithoutMfa()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, "[]");

    await Controller().List(CancellationToken.None);

    var jwt = SentToken();
    jwt.Subject.Should().Be("cus_42");
    jwt.Claims.Should().NotContain(c => c.Type == "mfa");
  }

  [Fact]
  public async Task List_ADifferentUser_IsSignedAsTheirOwnCustomer()
  {
    _user = new User { Id = 42, StripeCustomerId = "cus_other", HasMfaEnabled = true, IsEmailVerified = true };
    _handler.Respond = _ => Json(HttpStatusCode.OK, "[]");

    await Controller().List(CancellationToken.None);

    SentToken().Subject.Should().Be("cus_other");
  }

  [Fact]
  public async Task List_WithoutAStripeCustomer_Is409NoCloudAccount_AndCallsNothing()
  {
    _user = new User { Id = 42, StripeCustomerId = null, HasMfaEnabled = true };

    var result = await Controller().List(CancellationToken.None);

    Problem(result).Should().Be((409, "no_cloud_account"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task List_WhenThePortalIsNotConfigured_Is404NotAvailable()
  {
    var result = await Controller(key: "").List(CancellationToken.None);

    Problem(result).Should().Be((404, "not_available"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task List_UnknownUser_IsUnauthorized()
  {
    _user = null!;

    var result = await Controller().List(CancellationToken.None);

    result.Should().BeOfType<UnauthorizedResult>();
  }

  // ── create a code ──

  [Fact]
  public async Task CreateCode_WithoutStepUp_Is403MfaRequired_AndCallsNothing()
  {
    var result = await Controller().CreateCode(CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task CreateCode_WithABadOperationToken_Is403MfaRequired()
  {
    var result = await Controller(opToken: "forged").CreateCode(CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task CreateCode_ForAnAccountWithAnUnverifiedEmail_Is403EmailVerificationRequired()
  {
    _user = new User { Id = 42, StripeCustomerId = "cus_42", HasMfaEnabled = true, IsEmailVerified = false };

    var result = await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    Problem(result).Should().Be((403, "email_verification_required"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task CreateCode_APasskeyCountsAsASecondFactor()
  {
    _user = new User { Id = 42, StripeCustomerId = "cus_42", HasMfaEnabled = false, HasPasskeyRegistered = true, IsEmailVerified = true };
    _handler.Respond = _ => Json(HttpStatusCode.OK, """{"code":"ABCDE-FGHJK","expiresAt":"2026-10-31T12:15:00Z"}""");

    var result = await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
  }

  [Fact]
  public async Task CreateCode_AfterStepUp_ReturnsTheCodeDirectly_AndSignsWithMfa()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, """{"code":"ABCDE-FGHJK","expiresAt":"2026-10-31T12:15:00Z"}""");

    var result = await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    var code = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudPairingCode>().Subject;
    code.Code.Should().Be("ABCDE-FGHJK");
    code.ExpiresAt.Should().Be(new DateTime(2026, 10, 31, 12, 15, 0, DateTimeKind.Utc));
    _handler.Requests.Single().Method.Should().Be(HttpMethod.Post);
    _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/v1/pairing/codes");
    var jwt = SentToken();
    jwt.Subject.Should().Be("cus_42");
    jwt.Claims.Single(c => c.Type == "mfa").Value.Should().Be("true");
  }

  [Fact]
  public async Task CreateCode_ChecksTheTokenForTheCloudServersOperationOnly()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, """{"code":"X","expiresAt":"2026-10-31T12:15:00Z"}""");

    await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    _stepUp.Verify(s => s.ValidateOperationTokenAsync(ValidOpToken, ProtectedOperation.ManageCloudServers, 42), Times.Once);
  }

  [Fact]
  public async Task CreateCode_TooManyCodes_Is429WithTheBridgesMessage()
  {
    _handler.Respond = _ => Json(HttpStatusCode.BadRequest,
      """{"error":"too_many_codes","message":"Too many unused pairing codes. Wait for one to expire."}""");

    var result = await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    Problem(result).Should().Be((429, "too_many_codes"));
  }

  [Fact]
  public async Task CreateCode_WhenTheBridgeRefusesOurToken_Is502()
  {
    _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

    var result = await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    Problem(result).Should().Be((502, "cloud_auth_failed"));
  }

  [Fact]
  public async Task CreateCode_WhenTheBridgeIsDown_Is502()
  {
    _handler.Throw = new HttpRequestException("connection refused");

    var result = await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    Problem(result).Should().Be((502, "cloud_unavailable"));
  }

  [Fact]
  public async Task CreateCode_ABridgeErrorPage_IsNotEchoed()
  {
    _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>nginx</html>") };

    var result = await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    Problem(result).Should().Be((502, "cloud_unavailable"));
    ((ObjectResult)result).Value!.ToString().Should().NotContain("nginx");
  }

  // ── confirm ──

  [Fact]
  public async Task Confirm_WithoutStepUp_Is403_AndCallsNothing()
  {
    var result = await Controller().Confirm(Guid.NewGuid(), CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Confirm_AfterStepUp_PostsToThatServer_WithMfa()
  {
    var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
    _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

    var result = await Controller(opToken: ValidOpToken).Confirm(id, CancellationToken.None);

    result.Should().BeOfType<NoContentResult>();
    _handler.Requests.Single().Method.Should().Be(HttpMethod.Post);
    _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be($"/v1/pairing/servers/{id}/confirm");
    SentToken().Claims.Single(c => c.Type == "mfa").Value.Should().Be("true");
  }

  [Fact]
  public async Task Confirm_AServerNotOnTheAccount_Is404()
  {
    _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    var result = await Controller(opToken: ValidOpToken).Confirm(Guid.NewGuid(), CancellationToken.None);

    Problem(result).Should().Be((404, "not_found"));
  }

  // ── revoke ──

  [Fact]
  public async Task Revoke_DeletesThatServer_WithoutNeedingStepUp()
  {
    var id = Guid.Parse("22222222-2222-2222-2222-222222222222");
    _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

    var result = await Controller().Revoke(id, CancellationToken.None);

    result.Should().BeOfType<NoContentResult>();
    _handler.Requests.Single().Method.Should().Be(HttpMethod.Delete);
    _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be($"/v1/pairing/servers/{id}");
    var jwt = SentToken();
    jwt.Subject.Should().Be("cus_42");
    jwt.Claims.Should().NotContain(c => c.Type == "mfa");
  }

  [Fact]
  public async Task Revoke_AServerNotOnTheAccount_Is404()
  {
    _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    var result = await Controller().Revoke(Guid.NewGuid(), CancellationToken.None);

    Problem(result).Should().Be((404, "not_found"));
  }

  [Fact]
  public async Task Revoke_WithoutAStripeCustomer_Is409()
  {
    _user = new User { Id = 42, StripeCustomerId = "", HasMfaEnabled = true };

    var result = await Controller().Revoke(Guid.NewGuid(), CancellationToken.None);

    Problem(result).Should().Be((409, "no_cloud_account"));
    _handler.Requests.Should().BeEmpty();
  }

  // ── one-click link ──

  private const string Callback = "https://desktop-abc.tail1234.ts.net:10111/api/CloudBridge/link/callback";
  private const string CodeJson = """{"code":"ABCDE-FGHJK","expiresAt":"2026-10-31T12:15:00Z"}""";

  private static CloudServersController.LinkBody LinkBody(string? callback = Callback, string? state = "st_abc-123", string? name = "Home server") =>
    new(callback, state, name);

  [Fact]
  public async Task Link_AfterStepUp_MakesAnAutoConfirmCode_AndSendsTheBrowserBackWithIt()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, CodeJson);

    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(), CancellationToken.None);

    var link = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudServersController.LinkRedirect>().Subject;
    link.Redirect.Should().Be(Callback + "?code=ABCDE-FGHJK&state=st_abc-123");
    var request = _handler.Requests.Single();
    request.RequestUri!.AbsolutePath.Should().Be("/v1/pairing/codes");
    _handler.Bodies.Single().Should().Contain("\"autoConfirm\":true");
    SentToken().Claims.Single(c => c.Type == "mfa").Value.Should().Be("true");
  }

  [Fact]
  public async Task CreateCode_ByHand_DoesNotAskForAutoConfirm()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, CodeJson);

    await Controller(opToken: ValidOpToken).CreateCode(CancellationToken.None);

    _handler.Bodies.Single().Should().Be("{}");
  }

  [Fact]
  public async Task Link_EscapesTheStateInTheRedirect()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, CodeJson);

    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(state: "a&code=EVIL#x y"), CancellationToken.None);

    var redirect = ((CloudServersController.LinkRedirect)((OkObjectResult)result).Value!).Redirect;
    redirect.Should().Be(Callback + "?code=ABCDE-FGHJK&state=a%26code%3DEVIL%23x%20y");
  }

  [Fact]
  public async Task Link_AllowsPlainHttpOnlyForALoopbackServer()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, CodeJson);

    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(callback: "http://localhost:5000/api/CloudBridge/link/callback"), CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
  }

  [Fact]
  public async Task Link_WithoutStepUp_Is403MfaRequired_AndMakesNoCode()
  {
    var result = await Controller().Link(LinkBody(), CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Link_WithABadOperationToken_Is403MfaRequired()
  {
    var result = await Controller(opToken: "forged").Link(LinkBody(), CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Link_ForAnAccountWithAnUnverifiedEmail_Is403EmailVerificationRequired()
  {
    _user = new User { Id = 42, StripeCustomerId = "cus_42", HasMfaEnabled = true, IsEmailVerified = false };

    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(), CancellationToken.None);

    Problem(result).Should().Be((403, "email_verification_required"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Link_WithoutAStripeCustomer_Is409NoCloudAccount()
  {
    _user = new User { Id = 42, StripeCustomerId = null, HasMfaEnabled = true };

    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(), CancellationToken.None);

    Problem(result).Should().Be((409, "no_cloud_account"));
    _handler.Requests.Should().BeEmpty();
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("/api/CloudBridge/link/callback")]
  [InlineData("http://desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback")]
  [InlineData("http://192.168.1.20:8080/api/CloudBridge/link/callback")]
  [InlineData("ftp://desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback")]
  [InlineData("javascript:alert(1)//api/CloudBridge/link/callback")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/CloudBridge/pair")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/cloudbridge/link/callback")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback/")]
  [InlineData("https://desktop-abc.tail1234.ts.net/evil/api/CloudBridge/link/callback")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback?next=https://evil.test")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback?")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback#frag")]
  [InlineData("https://user:pw@desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback")]
  [InlineData("https://evil.test@desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/CloudBridge/link/../link/callback")]
  [InlineData("https://desktop-abc.tail1234.ts.net/api/CloudBridge%2Flink/callback")]
  [InlineData(" https://desktop-abc.tail1234.ts.net/api/CloudBridge/link/callback")]
  public async Task Link_ACallbackThatIsNotExactlyAServersLinkCallback_Is400(string? callback)
  {
    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(callback: callback), CancellationToken.None);

    Problem(result).Should().Be((400, "invalid_callback"));
    _handler.Requests.Should().BeEmpty();
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public async Task Link_WithoutAState_Is400(string? state)
  {
    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(state: state), CancellationToken.None);

    Problem(result).Should().Be((400, "invalid_state"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Link_AStateOver200Characters_Is400()
  {
    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(state: new string('s', 201)), CancellationToken.None);

    Problem(result).Should().Be((400, "invalid_state"));
  }

  [Fact]
  public async Task Link_ANameOver60Characters_Is400()
  {
    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(name: new string('n', 61)), CancellationToken.None);

    Problem(result).Should().Be((400, "invalid_name"));
  }

  [Fact]
  public async Task Link_ANameOf60CharactersAfterTrimming_IsFine()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, CodeJson);

    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(name: "  " + new string('n', 60) + "  "), CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
  }

  [Fact]
  public async Task Link_WhenTheCloudIsDown_Is502()
  {
    _handler.Throw = new HttpRequestException("connection refused");

    var result = await Controller(opToken: ValidOpToken).Link(LinkBody(), CancellationToken.None);

    Problem(result).Should().Be((502, "cloud_unavailable"));
  }

  // ── link by code (the Cloud portal at buoy-ai.com/cloud/link) ──

  private const string SessionJson = """
    {"name":"Home server","serverVersion":"1.5.36","createdAt":"2026-10-31T12:00:00Z","expiresAt":"2026-10-31T12:10:00Z","status":"pending"}
    """;

  [Theory]
  [InlineData("BCDF-GHJK", "BCDF-GHJK")]
  [InlineData("bcdfghjk", "BCDF-GHJK")]
  [InlineData(" bcd f-gh jk ", "BCDF-GHJK")]
  [InlineData("23456789", "2345-6789")]
  public void NormalizeUserCode_UppercasesAndRegroups(string raw, string expected)
  {
    CloudServersController.NormalizeUserCode(raw).Should().Be(expected);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("BCDF-GHJ")]
  [InlineData("BCDF-GHJKL")]
  [InlineData("ABCD-EFGH")] // vowels are never issued
  [InlineData("BCDF-GH0K")] // nor 0, O, 1 or I
  [InlineData("BCDF/../K")]
  public void NormalizeUserCode_RefusesAnythingElse(string? raw)
  {
    CloudServersController.NormalizeUserCode(raw).Should().BeNull();
  }

  [Fact]
  public async Task GetLinkSession_ShowsTheWaitingServer_SignedAsTheOwner_WithoutMfa()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, SessionJson);

    var result = await Controller().GetLinkSession("bcdf ghjk", CancellationToken.None);

    var session = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudLinkSession>().Subject;
    session.UserCode.Should().Be("BCDF-GHJK");
    session.Name.Should().Be("Home server");
    session.ServerVersion.Should().Be("1.5.36");
    session.Status.Should().Be("pending");
    session.ExpiresAt.Should().Be(new DateTime(2026, 10, 31, 12, 10, 0, DateTimeKind.Utc));
    _handler.Requests.Single().Method.Should().Be(HttpMethod.Get);
    _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/v1/pairing/link-sessions/BCDF-GHJK");
    var jwt = SentToken();
    jwt.Subject.Should().Be("cus_42");
    jwt.Claims.Should().NotContain(c => c.Type == "mfa");
  }

  [Fact]
  public async Task GetLinkSession_AMalformedCode_Is400_AndCallsNothing()
  {
    var result = await Controller().GetLinkSession("not-a-code", CancellationToken.None);

    Problem(result).Should().Be((400, "invalid_code"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task GetLinkSession_UnknownOrExpired_Is404InvalidCode()
  {
    _handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    var result = await Controller().GetLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((404, "invalid_code"));
  }

  [Fact]
  public async Task GetLinkSession_WithoutAStripeCustomer_Is409NoCloudAccount()
  {
    _user = new User { Id = 42, StripeCustomerId = null, HasMfaEnabled = true };

    var result = await Controller().GetLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((409, "no_cloud_account"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task GetLinkSession_WhenThePortalIsNotConfigured_Is404NotAvailable()
  {
    var result = await Controller(key: "").GetLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((404, "not_available"));
  }

  [Fact]
  public async Task ApproveLinkSession_WithoutStepUp_Is403MfaRequired_AndCallsNothing()
  {
    var result = await Controller().ApproveLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task ApproveLinkSession_WithAForgedOperationToken_Is403MfaRequired()
  {
    var result = await Controller(opToken: "forged").ApproveLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task ApproveLinkSession_ForAnAccountWithAnUnverifiedEmail_Is403EmailVerificationRequired()
  {
    _user = new User { Id = 42, StripeCustomerId = "cus_42", HasMfaEnabled = true, IsEmailVerified = false };

    var result = await Controller(opToken: ValidOpToken).ApproveLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((403, "email_verification_required"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task ApproveLinkSession_ACloudOnlyAccountWithNoAuthenticator_StillNeedsStepUp()
  {
    _user = new User { Id = 42, StripeCustomerId = "cus_42", HasMfaEnabled = false, HasPasskeyRegistered = false, IsEmailVerified = true };

    var result = await Controller().ApproveLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((403, "MfaRequired"));
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task ApproveLinkSession_ACloudOnlyAccountWithNoAuthenticator_ApprovesAfterTheEmailCodeStepUp()
  {
    _user = new User { Id = 42, StripeCustomerId = "cus_42", HasMfaEnabled = false, HasPasskeyRegistered = false, IsEmailVerified = true };
    _handler.Respond = _ => Json(HttpStatusCode.OK, SessionJson.Replace("pending", "approved"));

    var result = await Controller(opToken: ValidOpToken).ApproveLinkSession("BCDF-GHJK", CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>();
    _handler.Requests.Should().ContainSingle();
  }

  [Fact]
  public async Task ApproveLinkSession_AfterStepUp_ApprovesWithAnMfaOwnerToken_AndReturnsTheSession()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, SessionJson.Replace("pending", "approved"));

    var result = await Controller(opToken: ValidOpToken).ApproveLinkSession("bcdf-ghjk", CancellationToken.None);

    var session = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudLinkSession>().Subject;
    session.Status.Should().Be("approved");
    session.UserCode.Should().Be("BCDF-GHJK");
    _handler.Requests.Single().Method.Should().Be(HttpMethod.Post);
    _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/v1/pairing/link-sessions/BCDF-GHJK/approve");
    var jwt = SentToken();
    jwt.Subject.Should().Be("cus_42");
    jwt.Claims.Single(c => c.Type == "mfa").Value.Should().Be("true");
    _stepUp.Verify(s => s.ValidateOperationTokenAsync(ValidOpToken, ProtectedOperation.ManageCloudServers, 42), Times.Once);
  }

  [Fact]
  public async Task ApproveLinkSession_DecidedByAnotherOwnerOrADeny_Is409AlreadyDecided()
  {
    _handler.Respond = _ => Json(HttpStatusCode.Conflict, """{"error":"already_decided","message":"Already decided."}""");

    var result = await Controller(opToken: ValidOpToken).ApproveLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((409, "already_decided"));
  }

  [Fact]
  public async Task ApproveLinkSession_UnknownOrExpiredCode_Is404InvalidCode()
  {
    _handler.Respond = _ => Json(HttpStatusCode.NotFound, """{"error":"unknown_code","message":"Unknown code."}""");

    var result = await Controller(opToken: ValidOpToken).ApproveLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((404, "invalid_code"));
  }

  [Fact]
  public async Task GetLinkSession_TooManyWrongCodes_Is429RateLimited()
  {
    _handler.Respond = _ => Json(HttpStatusCode.TooManyRequests, """{"error":"rate_limited","message":"Slow down."}""");

    var result = await Controller().GetLinkSession("BCDF-GHJK", CancellationToken.None);

    Problem(result).Should().Be((429, "rate_limited"));
  }

  [Fact]
  public async Task DenyLinkSession_NeedsNoStepUp_AndSignsWithoutMfa()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, SessionJson.Replace("pending", "denied"));

    var result = await Controller().DenyLinkSession("BCDF-GHJK", CancellationToken.None);

    result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudLinkSession>().Which.Status.Should().Be("denied");
    _handler.Requests.Single().RequestUri!.AbsolutePath.Should().Be("/v1/pairing/link-sessions/BCDF-GHJK/deny");
    SentToken().Claims.Should().NotContain(c => c.Type == "mfa");
    _stepUp.Verify(s => s.ValidateOperationTokenAsync(It.IsAny<string>(), It.IsAny<ProtectedOperation>(), It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task DenyLinkSession_AMalformedCode_Is400()
  {
    var result = await Controller().DenyLinkSession("x", CancellationToken.None);

    Problem(result).Should().Be((400, "invalid_code"));
    _handler.Requests.Should().BeEmpty();
  }

  // ── who sees "Your servers" in the hosted app's Settings ──

  private static bool Shown(IActionResult result) =>
    result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<CloudServersController.Visibility>().Subject.Show;

  [Fact]
  public async Task Visibility_ACloudCustomer_SeesIt_WithoutAskingTheCloud()
  {
    _entitlements.Setup(e => e.HasCloudProductsAsync(42)).ReturnsAsync(true);

    Shown(await Controller().GetVisibility(CancellationToken.None)).Should().BeTrue();
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Visibility_NoAddOnButALinkedServer_SeesIt()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, ServersJson);

    Shown(await Controller().GetVisibility(CancellationToken.None)).Should().BeTrue();
  }

  [Fact]
  public async Task Visibility_NoAddOnAndOnlyRevokedServers_DoesNotSeeIt()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, """
      [{"id":"11111111-1111-1111-1111-111111111111","name":"Old box","serverVersion":"1.5.30","createdAt":"2026-09-01T10:00:00Z",
        "confirmedAt":"2026-09-01T10:05:00Z","lastSeenAt":null,"revokedAt":"2026-10-01T00:00:00Z","deliveryResumesAt":null}]
      """);

    Shown(await Controller().GetVisibility(CancellationToken.None)).Should().BeFalse();
  }

  [Fact]
  public async Task Visibility_AnOrdinaryHostedUser_DoesNotSeeIt()
  {
    _handler.Respond = _ => Json(HttpStatusCode.OK, "[]");

    Shown(await Controller().GetVisibility(CancellationToken.None)).Should().BeFalse();
  }

  [Fact]
  public async Task Visibility_NoStripeCustomer_DoesNotSeeIt_AndCallsNothing()
  {
    _user = new User { Id = 42, StripeCustomerId = null };

    Shown(await Controller().GetVisibility(CancellationToken.None)).Should().BeFalse();
    _handler.Requests.Should().BeEmpty();
  }

  [Fact]
  public async Task Visibility_WhenTheCloudIsDown_DoesNotSeeIt()
  {
    _handler.Throw = new HttpRequestException("connection refused");

    Shown(await Controller().GetVisibility(CancellationToken.None)).Should().BeFalse();
  }

  [Fact]
  public async Task Visibility_WhenThePortalIsNotConfigured_DoesNotSeeIt()
  {
    _entitlements.Setup(e => e.HasCloudProductsAsync(42)).ReturnsAsync(true);

    Shown(await Controller(key: "").GetVisibility(CancellationToken.None)).Should().BeFalse();
  }
}
