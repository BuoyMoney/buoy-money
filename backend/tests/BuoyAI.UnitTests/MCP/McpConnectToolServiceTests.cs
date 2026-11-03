using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.MCP;

/// <summary>
/// The public "BuoyAI Connect" MCP surface (RFC #782). These lock the security-critical contract:
/// only read-only tools are exposed, userId comes from the token (never the arguments), and every
/// tools/call is scope-, tier-, and quota-gated before dispatch.
/// </summary>
public class McpConnectToolServiceTests
{
  private const string ReadToolName = "get_budgets";
  private const string WriteToolName = "create_budget_category";

  // ---- tools/list projection (against the REAL registry) --------------------------------------

  private static McpConnectToolService WithRealRegistry()
  {
    // DiscoverTools() only reflects over the tool classes' attributes — it never resolves the tool
    // services — so an empty provider is enough to get the real tool metadata (names + ReadOnly).
    var provider = new ServiceCollection().BuildServiceProvider();
    var executor = new McpToolExecutor(provider, NullLogger<McpToolExecutor>.Instance);
    return new McpConnectToolService(
      executor, Mock.Of<IEntitlementService>(), Mock.Of<IAIUsageTracker>(),
      NullLogger<McpConnectToolService>.Instance);
  }

  [Fact]
  public void ListTools_ExposesKnownReadOnlyTool_WithObjectInputSchema()
  {
    var service = WithRealRegistry();

    var tools = service.ListTools().Tools;

    var readTool = tools.FirstOrDefault(t => t.Name == ReadToolName);
    readTool.Should().NotBeNull("read-only tools must be advertised on the public endpoint");
    readTool!.InputSchema.ValueKind.Should().Be(JsonValueKind.Object);
    readTool.InputSchema.GetProperty("type").GetString().Should().Be("object");
    readTool.InputSchema.TryGetProperty("properties", out _).Should().BeTrue();
    readTool.Annotations!.ReadOnlyHint.Should().BeTrue();
  }

  [Fact]
  public void ListTools_OmitsWriteTools()
  {
    var service = WithRealRegistry();

    var tools = service.ListTools().Tools;

    tools.Should().NotContain(t => t.Name == WriteToolName,
      "FINANCIAL write tools must never be exposed on the public Connect surface");
  }

  [Fact]
  public void ListTools_NeverAdvertisesTheInjectedUserIdParameter()
  {
    var service = WithRealRegistry();

    var readTool = service.ListTools().Tools.First(t => t.Name == ReadToolName);

    readTool.InputSchema.GetProperty("properties").TryGetProperty("userId", out _)
      .Should().BeFalse("userId is injected from the token, not supplied by the caller");
  }

  // ---- tools/call authorization + dispatch (mocked executor) ----------------------------------

  private static Mock<IMcpToolExecutor> ExecutorExposing(string toolName)
  {
    var executor = new Mock<IMcpToolExecutor>();
    executor.Setup(e => e.GetAvailableTools()).Returns(new List<McpToolDefinition>
    {
      new() { Name = toolName, Description = "read tool", ReadOnly = true, Parameters = new List<McpToolParameter>() },
    });
    executor
      .Setup(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(),
        It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync("{\"ok\":true}");
    return executor;
  }

  private static ClaimsPrincipal Principal(string userId, string? scope = "buoyai:read", string clientId = "claude")
  {
    var claims = new List<Claim> { new("nameid", userId), new("client_id", clientId) };
    if (scope != null) claims.Add(new Claim("scope", scope));
    return new ClaimsPrincipal(new ClaimsIdentity(claims, "McpBearer"));
  }

  private static string? TextOf(CallToolResult result) =>
    (result.Content.FirstOrDefault() as TextContentBlock)?.Text;

  [Fact]
  public async Task CallToolAsync_DispatchesWithTokenUserId_IgnoringUserIdInArguments()
  {
    var executor = ExecutorExposing(ReadToolName);
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);

    var service = new McpConnectToolService(executor.Object, billing.Object, usage.Object,
      NullLogger<McpConnectToolService>.Instance);

    // Token identifies user 1; the caller tries to smuggle user 999 via the arguments.
    var request = new CallToolRequestParams
    {
      Name = ReadToolName,
      Arguments = new Dictionary<string, JsonElement>
      {
        ["userId"] = JsonSerializer.SerializeToElement(999),
      },
    };

    var result = await service.CallToolAsync(Principal("1"), request);

    result.IsError.Should().NotBe(true);
    executor.Verify(e => e.ExecuteToolAsync(1, ReadToolName,
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Once);
    executor.Verify(e => e.ExecuteToolAsync(999, It.IsAny<string>(),
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task CallToolAsync_MissingScope_ReturnsErrorResult_AndNeverDispatches()
  {
    var executor = ExecutorExposing(ReadToolName);
    var service = new McpConnectToolService(executor.Object, Mock.Of<IEntitlementService>(),
      Mock.Of<IAIUsageTracker>(), NullLogger<McpConnectToolService>.Instance);

    var request = new CallToolRequestParams { Name = ReadToolName };
    var result = await service.CallToolAsync(Principal("1", scope: null), request);

    result.IsError.Should().BeTrue();
    TextOf(result).Should().Contain("buoyai:read");
    executor.Verify(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(),
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task CallToolAsync_FamilyMemberOnPremiumInherited_IsAllowed()
  {
    var executor = ExecutorExposing(ReadToolName);
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.PremiumInherited);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);

    var service = new McpConnectToolService(executor.Object, billing.Object, usage.Object,
      NullLogger<McpConnectToolService>.Instance);

    var result = await service.CallToolAsync(Principal("1"), new CallToolRequestParams { Name = ReadToolName });

    result.IsError.Should().NotBe(true);
    executor.Verify(e => e.ExecuteToolAsync(1, ReadToolName,
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Once);
  }

  [Fact]
  public async Task CallToolAsync_NonPremiumTier_ReturnsErrorResult_AndNeverDispatches()
  {
    var executor = ExecutorExposing(ReadToolName);
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Standard);

    var service = new McpConnectToolService(executor.Object, billing.Object,
      Mock.Of<IAIUsageTracker>(), NullLogger<McpConnectToolService>.Instance);

    var request = new CallToolRequestParams { Name = ReadToolName };
    var result = await service.CallToolAsync(Principal("1"), request);

    result.IsError.Should().BeTrue();
    TextOf(result).Should().Contain("Premium");
    executor.Verify(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(),
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task CallToolAsync_OverQuota_ReturnsRateLimitError_AndNeverDispatches()
  {
    var executor = ExecutorExposing(ReadToolName);
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(false);

    var service = new McpConnectToolService(executor.Object, billing.Object, usage.Object,
      NullLogger<McpConnectToolService>.Instance);

    var result = await service.CallToolAsync(Principal("1"), new CallToolRequestParams { Name = ReadToolName });

    result.IsError.Should().BeTrue();
    TextOf(result).Should().Contain("Rate limit");
    executor.Verify(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(),
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task CallToolAsync_UnknownOrWriteTool_ReturnsErrorResult()
  {
    // Executor advertises only the read tool; asking for a write tool must be rejected pre-dispatch.
    var executor = ExecutorExposing(ReadToolName);
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);

    var service = new McpConnectToolService(executor.Object, billing.Object, usage.Object,
      NullLogger<McpConnectToolService>.Instance);

    var result = await service.CallToolAsync(Principal("1"), new CallToolRequestParams { Name = WriteToolName });

    result.IsError.Should().BeTrue();
    executor.Verify(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(),
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task CallToolAsync_Success_RecordsUsageWithMcpContext()
  {
    var executor = ExecutorExposing(ReadToolName);
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);

    var service = new McpConnectToolService(executor.Object, billing.Object, usage.Object,
      NullLogger<McpConnectToolService>.Instance);

    var result = await service.CallToolAsync(Principal("7"), new CallToolRequestParams { Name = ReadToolName });

    result.IsError.Should().NotBe(true);
    usage.Verify(u => u.RecordUsageAsync(7, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<decimal>(),
      McpConnectToolService.UsageContext), Times.Once);
  }

  // ---- cross-agent memory write surface (the one non-financial public write) -------------------

  [Fact]
  public void ListTools_ExposesRememberWriteTool_WithReadOnlyHintFalse()
  {
    var service = WithRealRegistry();

    var remember = service.ListTools().Tools.FirstOrDefault(t => t.Name == "remember");

    remember.Should().NotBeNull("the non-financial memory write tool is exposed on Connect");
    remember!.Annotations!.ReadOnlyHint.Should().BeFalse("remember writes a note, so it must be advertised honestly as not read-only");
  }

  private static Mock<IMcpToolExecutor> ExecutorExposingWrite(string toolName)
  {
    var executor = new Mock<IMcpToolExecutor>();
    executor.Setup(e => e.GetAvailableTools()).Returns(new List<McpToolDefinition>
    {
      new() { Name = toolName, Description = "memory write", ReadOnly = false, Parameters = new List<McpToolParameter>() },
    });
    executor
      .Setup(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(),
        It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync("{\"Remembered\":true}");
    return executor;
  }

  [Fact]
  public async Task CallToolAsync_Remember_WithOnlyReadScope_IsRejected_AndNeverDispatches()
  {
    var executor = ExecutorExposingWrite("remember");
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);
    var service = new McpConnectToolService(executor.Object, billing.Object, usage.Object,
      NullLogger<McpConnectToolService>.Instance);

    // A read-only grant (e.g. a user who connected before the write scope existed) can't write.
    var result = await service.CallToolAsync(
      Principal("1", scope: "buoyai:read"), new CallToolRequestParams { Name = "remember" });

    result.IsError.Should().BeTrue();
    TextOf(result).Should().Contain("buoyai:write:memory");
    executor.Verify(e => e.ExecuteToolAsync(It.IsAny<int>(), It.IsAny<string>(),
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Never);
  }

  [Fact]
  public async Task CallToolAsync_Remember_WithWriteScope_PassesScopeGate_AndDispatches()
  {
    var executor = ExecutorExposingWrite("remember");
    var billing = new Mock<IEntitlementService>();
    billing.Setup(b => b.GetEffectiveTierAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);
    var service = new McpConnectToolService(executor.Object, billing.Object, usage.Object,
      NullLogger<McpConnectToolService>.Instance);

    var result = await service.CallToolAsync(
      Principal("1", scope: "buoyai:read buoyai:write:memory"),
      new CallToolRequestParams { Name = "remember" });

    result.IsError.Should().NotBe(true);
    executor.Verify(e => e.ExecuteToolAsync(1, "remember",
      It.IsAny<Dictionary<string, object>?>(), It.IsAny<CancellationToken>()), Times.Once);
  }
}
