using System.Security.Claims;
using BuoyAI.API.Controllers;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace BuoyAI.UnitTests.Controllers;

/// <summary>An AIController for user 123 on a phone, with its collaborators as mocks.</summary>
internal sealed class AIControllerTestHost
{
  public const int UserId = 123;
  public Mock<IAIService> AiService { get; } = new();
  public Mock<IEntitlementService> Entitlements { get; } = new();
  public Mock<IMcpToolExecutor> ToolExecutor { get; } = new();
  public Mock<IAiToolSecurityService> Security { get; } = new();
  public AIController Controller { get; }

  public AIControllerTestHost(bool requireConfirmationToken = false)
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Ai:RequireConfirmationToken"] = requireConfirmationToken ? "true" : "false",
      })
      .Build();

    Controller = new AIController(
      AiService.Object,
      new Mock<IAICategorizationService>().Object,
      ToolExecutor.Object,
      new Mock<IUserRepository>().Object,
      Security.Object,
      new Mock<IUploadQuotaService>().Object,
      new Mock<IInsightActionService>().Object,
      Entitlements.Object,
      config,
      new Mock<ILogger<AIController>>().Object);

    var http = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) }, "TestAuth"))
    };
    http.Request.Headers["X-Client-Type"] = "mobile-android";
    Controller.ControllerContext = new ControllerContext { HttpContext = http };
  }
}
