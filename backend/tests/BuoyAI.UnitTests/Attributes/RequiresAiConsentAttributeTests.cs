using System.Security.Claims;
using BuoyAI.API.Attributes;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Attributes;

public class RequiresAiConsentAttributeTests
{
  private sealed class Harness
  {
    public ActionExecutingContext Ctx = null!;
    public bool NextCalled;
    public ActionExecutionDelegate Next = null!;
    public Mock<IUserRepository> Repo = null!;
  }

  private static Harness Build(User? user, string? claim = "7", string? clientType = null)
  {
    var h = new Harness { Repo = new Mock<IUserRepository>() };
    h.Repo.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync(user);
    h.Repo.Setup(r => r.UpdateAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

    var services = new ServiceCollection().AddSingleton(h.Repo.Object).AddLogging().BuildServiceProvider();
    var identity = claim == null
      ? new ClaimsIdentity()
      : new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, claim) }, "test");
    var http = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(identity) };
    if (clientType != null) http.Request.Headers[RequiresAiConsentAttribute.ClientTypeHeader] = clientType;

    var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
    h.Ctx = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    h.Next = () =>
    {
      h.NextCalled = true;
      return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
    };
    return h;
  }

  private static User UserWith(DateTime? granted, DateTime? withdrawn = null, int? version = AiConsent.CurrentVersion) => new()
  {
    Id = 7, Email = "u@example.com", PasswordHash = "h",
    AiConsentGrantedAt = granted, AiConsentWithdrawnAt = withdrawn,
    AiConsentVersion = granted != null ? version : null
  };

  private static void ShouldBeRefused(Harness h)
  {
    h.NextCalled.Should().BeFalse();
    var result = h.Ctx.Result.Should().BeOfType<ObjectResult>().Subject;
    result.StatusCode.Should().Be(403);
    result.Value!.ToString().Should().Contain(RequiresAiConsentAttribute.ErrorCode);
  }

  [Fact]
  public async Task Undecided_OnIos_IsRefused()
  {
    var h = Build(UserWith(null), clientType: "mobile-ios");
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    ShouldBeRefused(h);
    h.Repo.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task Undecided_OnAndroid_IsRefused_NoImpliedGrant()
  {
    // An implied grant on Android used to count on iOS too (consent is per user), which is
    // how App Review's shared demo account reached AI Chat without seeing the sheet.
    var user = UserWith(null);
    var h = Build(user, clientType: "mobile-android");
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    ShouldBeRefused(h);
    user.AiConsentGrantedAt.Should().BeNull();
    h.Repo.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
  }

  [Fact]
  public async Task Undecided_NoHeader_IsRefused()
  {
    var h = Build(UserWith(null));
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    ShouldBeRefused(h);
  }

  [Fact]
  public async Task GrantedUnderAnOlderDisclosure_IsRefused()
  {
    // Grants from before versioning (null) were implied or given on a sheet that listed only
    // part of what is sent; they must be asked again on the current sheet.
    var h = Build(UserWith(DateTime.UtcNow.AddDays(-30), version: null), clientType: "mobile-ios");
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    ShouldBeRefused(h);
  }

  [Fact]
  public async Task Granted_OnAndroid_Proceeds()
  {
    var h = Build(UserWith(DateTime.UtcNow.AddDays(-1)), clientType: "mobile-android");
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    h.NextCalled.Should().BeTrue();
  }

  [Fact]
  public async Task Withdrawn_OnAndroid_IsRefused()
  {
    var h = Build(UserWith(null, DateTime.UtcNow), clientType: "mobile-android");
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    ShouldBeRefused(h);
  }

  [Fact]
  public async Task Granted_OnIos_Proceeds()
  {
    var h = Build(UserWith(DateTime.UtcNow.AddDays(-1)), clientType: "mobile-ios");
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    h.NextCalled.Should().BeTrue();
    h.Ctx.Result.Should().BeNull();
  }

  [Fact]
  public async Task UnknownUser_Returns401()
  {
    var h = Build(null);
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    h.NextCalled.Should().BeFalse();
    h.Ctx.Result.Should().BeOfType<UnauthorizedResult>();
  }

  [Fact]
  public async Task NoUserClaim_Returns401()
  {
    var h = Build(UserWith(DateTime.UtcNow), claim: null);
    await new RequiresAiConsentAttribute().OnActionExecutionAsync(h.Ctx, h.Next);
    h.NextCalled.Should().BeFalse();
    h.Ctx.Result.Should().BeOfType<UnauthorizedResult>();
  }
}
