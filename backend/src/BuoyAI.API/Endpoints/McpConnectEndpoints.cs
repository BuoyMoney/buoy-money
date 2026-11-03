using BuoyAI.Core.Configuration;
using BuoyAI.Infrastructure.MCP;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace BuoyAI.API.Endpoints;

/// <summary>
/// SDK wiring for the public "BuoyAI Connect" MCP endpoint (RFC #782). Kept thin: all behaviour
/// (which tools, authz, metering, auditing) lives in <see cref="McpConnectToolService"/> so it is
/// unit-testable without an HTTP host. Registered and mapped only when <c>Mcp:Enabled</c> is true
/// (Program.cs gates both calls), so a disabled deployment exposes no /mcp route at all.
///
/// The server runs in STATELESS HTTP mode (no server-held session) and does NOT auto-scan tool
/// assemblies — tools/list and tools/call are served by explicit handlers that project the
/// existing <see cref="IMcpToolExecutor"/> registry (read-only subset only).
/// </summary>
public static class McpConnectEndpoints
{
  /// <summary>Name of the JWT bearer scheme dedicated to Connect tokens (MCP-pinned iss/aud).</summary>
  public const string BearerScheme = "McpBearer";

  /// <summary>Authorization policy the /mcp endpoint requires — Connect tokens only.</summary>
  public const string AuthPolicy = "McpConnect";

  /// <summary>Rate-limiter policy name applied to /mcp (30/min per user).</summary>
  public const string RateLimitPolicy = "mcp";

  /// <summary>
  /// The MCP server-instructions primer, served at <c>initialize</c>. Its job is to give a
  /// connecting agent BuoyAI's mental model up front so it reasons about the data correctly and
  /// advises in BuoyAI's voice. Kept provider-agnostic and concept-first (not tool docs — the
  /// tools carry their own descriptions).
  /// </summary>
  public const string ServerInstructions =
    """
    You are connected to Buoy Money, a personal-finance app that helps people budget, save, and
    understand their money. This connection is READ-ONLY for money: you can read the user's
    financial data to answer questions and give advice, but you cannot move money, change budgets,
    or edit any financial data. The one thing you CAN write is memory: use the "remember" tool to
    store durable facts the user tells you (a savings goal, a preference, a family detail, an
    important date) so Buoy Money recalls them next time, on this assistant or any other. Remembering
    stores a note and moves no money. Call "recall_memory" at the start of a money conversation to
    see what Buoy Money already knows. All amounts are already in the user's own currency.

    HOW BUOY MONEY THINKS ABOUT BUDGETING (important: it is not a normal envelope app):

    • Count-up budgeting is the signature model. Instead of a category having a fixed monthly
      limit you spend DOWN from, a "count-up" category earns a small DAILY ALLOWANCE that
      ACCUMULATES — the available amount counts UP over the month. Spending draws from that
      accumulated pool. So a healthy count-up category is one where the accumulated allowance is
      growing faster than spending. Frame this positively: the user is building a cushion, not
      burning down a limit. (get_countup_categories exposes the daily allowance + accumulated
      amount; not every category is count-up — traditional fixed-limit categories also exist.)

    • Rollover: unspent money in a category can carry over to the next period instead of resetting
      to zero — so "unused" budget is a saved asset, not lost.

    • Budget structure: a user has budgets containing categories. Budgets can be hierarchical
      (a primary/parent budget with child budgets), and can be shared with a family (members have
      roles — some can edit, some are read-only). Respect that shared context when advising.

    • Savings goals are separate from budget categories: named targets (e.g. "Emergency Fund")
      that money is allocated toward, tracked by progress to a target amount.

    READING THE DATA CORRECTLY:

    • For the real picture of money in vs. out, prefer get_budget_overview and get_cash_flow.
      "Spending" figures exclude transfers between the user's own accounts and exclude income —
      don't count a transfer as an expense.
    • Balances come from linked bank accounts (via Plaid) and may be slightly stale; say "as of the
      latest sync" rather than implying real-time.
    • Subscriptions and recurring income are detected heuristically — treat them as strong
      suggestions, not certainties, and let the user confirm.

    ADVISING IN BUOY MONEY'S VOICE: be encouraging and constructive. Celebrate accumulated allowances,
    rollover savings, and goal progress; when spending is high, suggest adjustments without shaming.
    Ground every claim in the tools — call them rather than guessing, and if data is missing, say so.
    """;

  public static IServiceCollection AddBuoyAiConnectMcp(this IServiceCollection services)
  {
    // Scoped: the list/call handlers resolve it per request so it can consume scoped billing/usage.
    services.AddScoped<McpConnectToolService>();

    services
      .AddMcpServer(options =>
      {
        options.ServerInfo = new Implementation { Name = Brand.Name, Version = "1.0.0" };
        // Server-level primer returned at initialize (#782 follow-up). Teaches the connecting
        // agent BuoyAI's opinionated model — especially count-up budgeting — so it interprets the
        // read-only data and gives on-brand advice instead of generic budgeting boilerplate.
        options.ServerInstructions = Brand.Apply(ServerInstructions);
      })
      .WithHttpTransport(options => options.Stateless = true)
      .WithListToolsHandler((context, _) =>
      {
        var service = context.Services!.GetRequiredService<McpConnectToolService>();
        return ValueTask.FromResult(service.ListTools());
      })
      .WithCallToolHandler(async (context, cancellationToken) =>
      {
        var service = context.Services!.GetRequiredService<McpConnectToolService>();
        return await service.CallToolAsync(context.User, context.Params, cancellationToken);
      });

    return services;
  }
}
