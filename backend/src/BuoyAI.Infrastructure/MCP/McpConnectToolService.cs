using System.Security.Claims;
using System.Text.Json;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace BuoyAI.Infrastructure.MCP;

/// <summary>
/// Projects BuoyAI's MCP tool registry onto the public "BuoyAI Connect" MCP endpoint (RFC #782)
/// and enforces its authorization contract on every <c>tools/call</c>.
///
/// This is the single source of truth for what the public endpoint exposes and how it behaves,
/// deliberately separated from the SDK wiring (AddMcpServer / MapMcp) so it can be unit-tested
/// without an HTTP host:
/// <list type="bullet">
///   <item>Only tools whose registry metadata says <c>ReadOnly</c> are advertised (tools/list) or
///   dispatchable (tools/call) — the public surface is strictly read-only.</item>
///   <item><c>userId</c> comes exclusively from the validated token's <c>nameid</c> claim; any
///   <c>userId</c> supplied in the call arguments is ignored (the executor force-injects the
///   token userId, and we never read a caller-supplied one).</item>
///   <item>Per call: the <c>buoyai:read</c> scope is required, the user must be Premium/Admin
///   (same tier gate as AIController.IsChatTierAsync), and the AI usage quota is checked and
///   recorded with request-context "mcp".</item>
///   <item>Tool failures surface as MCP error results (isError:true) carrying only a short
///   message — never a stack trace.</item>
/// </list>
///
/// Registered <c>Scoped</c>: the SDK's list/call handler delegates resolve it from the per-request
/// service provider so it can consume the scoped billing / usage services.
/// </summary>
public sealed class McpConnectToolService
{
  /// <summary>The read scope; every read tool call's token must carry it.</summary>
  public const string RequiredScope = "buoyai:read";

  /// <summary>The narrow write scope for the single non-financial write tool (remember).
  /// A read-only token (e.g. a user who connected before this scope existed) cannot invoke it.</summary>
  public const string MemoryWriteScope = "buoyai:write:memory";

  /// <summary>The only write tools BuoyAI Connect exposes. Non-financial by construction — they
  /// store personal notes and can never move money. Kept explicit so a new write tool can't leak
  /// onto the public surface by accident.</summary>
  private static readonly IReadOnlySet<string> ExposedWriteTools =
    new HashSet<string>(StringComparer.Ordinal) { "remember" };

  /// <summary>The scope a given tool call requires: the memory-write scope for exposed write
  /// tools, the read scope for everything else.</summary>
  private static string RequiredScopeFor(string toolName) =>
    ExposedWriteTools.Contains(toolName) ? MemoryWriteScope : RequiredScope;

  /// <summary>Usage-record tag distinguishing Connect tool calls from first-party chat.</summary>
  public const string UsageContext = "mcp";

  private readonly IMcpToolExecutor _executor;
  private readonly IEntitlementService _entitlements;
  private readonly IAIUsageTracker _usageTracker;
  private readonly ILogger<McpConnectToolService> _logger;

  public McpConnectToolService(
    IMcpToolExecutor executor,
    IEntitlementService entitlements,
    IAIUsageTracker usageTracker,
    ILogger<McpConnectToolService> logger)
  {
    _executor = executor;
    _entitlements = entitlements;
    _usageTracker = usageTracker;
    _logger = logger;
  }

  /// <summary>
  /// tools/list — the read-only subset of the registry, each projected to an MCP <see cref="Tool"/>
  /// with a JSON Schema input schema derived from the same parameter reflection the Gemini
  /// declarations use (the <c>userId</c> parameter is never advertised — it is injected server-side).
  /// </summary>
  public ListToolsResult ListTools()
  {
    var tools = new List<Tool>();
    foreach (var def in _executor.GetAvailableTools().Where(IsExposed))
    {
      tools.Add(new Tool
      {
        Name = def.Name,
        Description = def.Description,
        InputSchema = BuildInputSchema(def),
        // Honest hint: the one exposed write tool (remember) advertises ReadOnlyHint=false so
        // clients and users see it can write, even though it only writes notes, never money.
        Annotations = new ToolAnnotations { ReadOnlyHint = def.ReadOnly },
      });
    }
    return new ListToolsResult { Tools = tools };
  }

  /// <summary>
  /// tools/call — validates authorization, dispatches through the executor using the token-derived
  /// userId, meters usage, and emits one structured audit line. Returns an MCP error result rather
  /// than throwing for every expected failure (missing scope, wrong tier, over quota, unknown tool,
  /// tool error).
  /// </summary>
  public async Task<CallToolResult> CallToolAsync(
    ClaimsPrincipal? user, CallToolRequestParams? request, CancellationToken cancellationToken = default)
  {
    var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    var toolName = request?.Name ?? string.Empty;
    var clientId = user?.FindFirst("client_id")?.Value ?? "unknown";

    // userId is ALWAYS from the token — never from arguments.
    var userId = ResolveUserId(user);
    if (userId == null)
      return Error("Not authenticated.");

    if (string.IsNullOrWhiteSpace(toolName) || !IsExposed(toolName))
      return Error($"Unknown or unavailable tool: {toolName}");

    // Scope is checked per-tool once the tool is known, BEFORE tier/quota so an under-scoped token
    // is rejected regardless of subscription state: read tools require buoyai:read, the remember
    // tool requires buoyai:write:memory. A read-only token (e.g. an older grant) can't reach it.
    var requiredScope = RequiredScopeFor(toolName);
    if (!HasScope(user!, requiredScope))
      return Error($"Missing required scope '{requiredScope}'.");

    var tier = await _entitlements.GetEffectiveTierAsync(userId.Value);
    if (!tier.HasPremiumAccess())
      return Error("BuoyAI Connect requires a Premium subscription.");

    if (!await _usageTracker.CanMakeRequestAsync(userId.Value, tier.Value))
      return Error("Rate limit reached. Please try again later.");

    var arguments = ConvertArguments(request!.Arguments);
    bool success;
    CallToolResult result;
    try
    {
      var raw = await _executor.ExecuteToolAsync(userId.Value, toolName, arguments, cancellationToken);
      success = !LooksLikeToolError(raw, out var toolErrorMessage);
      result = success
        ? Ok(raw)
        : Error(string.IsNullOrWhiteSpace(toolErrorMessage) ? "The tool could not complete the request." : toolErrorMessage!);
    }
    catch (Exception ex)
    {
      // Defensive: the executor normally returns errors as data, but never leak a stack trace.
      _logger.LogError(ex, "BuoyAI Connect tool {Tool} threw for user {UserId}", toolName, userId);
      success = false;
      result = Error("The tool could not complete the request.");
    }

    // Meter the call against the user's AI quota, tagged as an MCP-sourced request.
    await _usageTracker.RecordUsageAsync(userId.Value, "mcp", 0, 0m, UsageContext);

    var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
    _logger.LogInformation(
      "BuoyAI Connect tools/call: userId={UserId} clientId={ClientId} tool={Tool} durationMs={DurationMs} success={Success}",
      userId.Value, clientId, toolName, Math.Round(elapsedMs, 1), success);

    return result;
  }

  // -------------------------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------------------------

  /// <summary>A read-only tool, or the one exposed non-financial write tool.</summary>
  private static bool IsExposed(McpToolDefinition def) =>
    def.ReadOnly || ExposedWriteTools.Contains(def.Name);

  private bool IsExposed(string toolName) =>
    _executor.GetAvailableTools().Any(t => IsExposed(t) && string.Equals(t.Name, toolName, StringComparison.Ordinal));

  private static int? ResolveUserId(ClaimsPrincipal? user)
  {
    var raw = user?.FindFirst("nameid")?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    return int.TryParse(raw, out var id) ? id : null;
  }

  private static bool HasScope(ClaimsPrincipal user, string requiredScope)
  {
    // The scope claim is a space-delimited list per OAuth 2.1; accept it as one or many claims.
    foreach (var claim in user.FindAll("scope"))
    {
      var scopes = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if (scopes.Contains(requiredScope, StringComparer.Ordinal))
        return true;
    }
    return false;
  }

  /// <summary>Maps the SDK's <c>IDictionary&lt;string, JsonElement&gt;</c> arguments to the executor's
  /// loosely-typed bag. A caller-supplied <c>userId</c> is intentionally NOT stripped here — the
  /// executor force-injects the token userId over any argument of that name — but we never read it.</summary>
  private static Dictionary<string, object>? ConvertArguments(IDictionary<string, JsonElement>? arguments)
  {
    if (arguments == null || arguments.Count == 0)
      return null;
    var bag = new Dictionary<string, object>(arguments.Count);
    foreach (var (key, value) in arguments)
      bag[key] = value; // JsonElement — the executor's ConvertArgument understands it.
    return bag;
  }

  private JsonElement BuildInputSchema(McpToolDefinition def)
  {
    var properties = new Dictionary<string, object>();
    var required = new List<string>();
    foreach (var param in def.Parameters.Where(p => !string.Equals(p.Name, "userId", StringComparison.OrdinalIgnoreCase)))
    {
      properties[param.Name] = new
      {
        type = param.Type,
        description = string.IsNullOrWhiteSpace(param.Description) ? $"The {param.Name} parameter" : param.Description,
      };
      if (param.Required)
        required.Add(param.Name);
    }

    object schema = required.Count > 0
      ? new { type = "object", properties, required }
      : new { type = "object", properties };
    return JsonSerializer.SerializeToElement(schema);
  }

  /// <summary>The executor swallows exceptions and returns <c>{"Error": "..."}</c> (message only, no
  /// stack trace). Detect that shape so it becomes an MCP error result instead of a success payload.</summary>
  private static bool LooksLikeToolError(string raw, out string? message)
  {
    message = null;
    if (string.IsNullOrWhiteSpace(raw))
      return false;
    try
    {
      using var doc = JsonDocument.Parse(raw);
      if (doc.RootElement.ValueKind != JsonValueKind.Object)
        return false;
      if (doc.RootElement.TryGetProperty("Error", out var err) || doc.RootElement.TryGetProperty("error", out err))
      {
        message = err.ValueKind == JsonValueKind.String ? err.GetString() : "The tool could not complete the request.";
        return true;
      }
    }
    catch (JsonException)
    {
      // Not JSON — treat as a plain successful text payload.
    }
    return false;
  }

  private static CallToolResult Ok(string json) => new()
  {
    IsError = false,
    Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
  };

  private static CallToolResult Error(string message) => new()
  {
    IsError = true,
    Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
  };
}
