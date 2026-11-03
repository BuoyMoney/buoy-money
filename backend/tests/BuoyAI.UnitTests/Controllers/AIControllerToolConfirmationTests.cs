using System.Text.Json;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

/// <summary>
/// POST /api/ai/tools/execute is the only place an AI write happens: the user's Confirm tap. A
/// confirmation token issued with the preview binds the tap to the tool and arguments the user saw.
/// </summary>
public class AIControllerToolConfirmationTests
{
  private const int UserId = AIControllerTestHost.UserId;
  private static readonly Guid Token = Guid.NewGuid();

  private static PendingToolConfirmation Pending(string tool = "update_budget_amount") => new()
  {
    Id = 42,
    UserId = UserId,
    ConfirmationToken = Token,
    ToolName = tool,
    ArgumentsJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["categoryId"] = 5, ["newAmount"] = 400 }),
  };

  [Fact]
  public async Task ValidToken_RunsTheStoredArguments_NotTheRequests()
  {
    var host = new AIControllerTestHost();
    host.Security.Setup(s => s.ValidateAndConsumeTokenAsync(UserId, Token, It.IsAny<CancellationToken>())).ReturnsAsync(Pending());
    Dictionary<string, object>? ran = null;
    host.ToolExecutor
      .Setup(e => e.ExecuteConfirmedToolAsync(UserId, "update_budget_amount", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
      .Callback<int, string, Dictionary<string, object>?, CancellationToken>((_, _, a, _) => ran = a)
      .ReturnsAsync("{\"Success\":true}");

    var result = await host.Controller.ExecuteTool(new ExecuteToolRequest
    {
      ToolName = "update_budget_amount",
      ConfirmationToken = Token,
      // A tampered request: the server must ignore these and run what was previewed.
      Arguments = new Dictionary<string, object> { ["categoryId"] = 5, ["newAmount"] = 1 },
    });

    result.Should().BeOfType<OkObjectResult>();
    ran.Should().NotBeNull();
    ran!["newAmount"].ToString().Should().Be("400");
    host.Security.Verify(s => s.UpdateConfirmationResultAsync(42, "{\"Success\":true}", true, It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task InvalidOrReusedToken_IsRefused()
  {
    var host = new AIControllerTestHost();
    host.Security.Setup(s => s.ValidateAndConsumeTokenAsync(UserId, Token, It.IsAny<CancellationToken>())).ReturnsAsync((PendingToolConfirmation?)null);

    var result = await host.Controller.ExecuteTool(new ExecuteToolRequest { ToolName = "update_budget_amount", ConfirmationToken = Token });

    result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeEquivalentTo(new { code = "INVALID_TOKEN" }, o => o.ExcludingMissingMembers());
    host.ToolExecutor.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task TokenForAnotherTool_IsRefused()
  {
    var host = new AIControllerTestHost();
    host.Security.Setup(s => s.ValidateAndConsumeTokenAsync(UserId, Token, It.IsAny<CancellationToken>())).ReturnsAsync(Pending("update_budget_amount"));

    var result = await host.Controller.ExecuteTool(new ExecuteToolRequest { ToolName = "delete_transaction", ConfirmationToken = Token });

    result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeEquivalentTo(new { code = "TOKEN_MISMATCH" }, o => o.ExcludingMissingMembers());
    host.ToolExecutor.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task NoToken_WhenRequired_IsRefused()
  {
    var host = new AIControllerTestHost(requireConfirmationToken: true);

    var result = await host.Controller.ExecuteTool(new ExecuteToolRequest
    {
      ToolName = "update_budget_amount",
      Arguments = new Dictionary<string, object> { ["categoryId"] = 5, ["newAmount"] = 400 },
    });

    result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeEquivalentTo(new { code = "CONFIRMATION_TOKEN_REQUIRED" }, o => o.ExcludingMissingMembers());
    host.ToolExecutor.VerifyNoOtherCalls();
  }

  [Fact]
  public async Task NoToken_FromAnOlderApp_StillConfirmsWhileAllowed()
  {
    var host = new AIControllerTestHost(requireConfirmationToken: false);
    host.ToolExecutor
      .Setup(e => e.ExecuteConfirmedToolAsync(UserId, "update_budget_amount", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync("{\"Success\":true}");

    var result = await host.Controller.ExecuteTool(new ExecuteToolRequest
    {
      ToolName = "update_budget_amount",
      Arguments = new Dictionary<string, object> { ["categoryId"] = 5, ["newAmount"] = 400 },
    });

    result.Should().BeOfType<OkObjectResult>();
    host.ToolExecutor.Verify(e => e.ExecuteConfirmedToolAsync(UserId, "update_budget_amount", It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()), Times.Once);
    host.ToolExecutor.Verify(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
  }
}
