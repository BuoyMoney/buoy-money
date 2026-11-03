using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.Analysis;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// The AI's source of truth for what the user actually earned and spent.
///
/// This exists because the model had no way to get it. It had budget allocations (what the user
/// PLANNED) and per-category actuals, but no actual totals — so when asked "how will I finish the
/// month?" it reported the budget as though it were reality, giving the Salary allocation as
/// income received and the sum of the expense allocations as money spent, while the real figures
/// were thousands of dollars apart. The projected surplus was
/// out by roughly 10x, and it recommended moving money on the strength of it.
///
/// Summing the raw transactions instead is also wrong — see CashFlowCalculator for why credit-card
/// payments and transfers have to come out of both sides.
/// </summary>
[McpServerToolType]
public sealed class CashFlowReadTools
{
    private readonly ITransactionRepository _transactionRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<CashFlowReadTools> _logger;

    public CashFlowReadTools(
        ITransactionRepository transactionRepository,
        IUserRepository userRepository,
        ILogger<CashFlowReadTools> logger)
    {
        _transactionRepository = transactionRepository;
        _userRepository = userRepository;
        _logger = logger;
    }

    [McpServerTool(Name = "get_cash_flow", ReadOnly = true)]
    [Description(
        "ACTUAL money received and spent over a period, from real transactions. " +
        "This is the ONLY correct source for questions like 'how much have I earned/spent this month', " +
        "'how will I finish the month', or 'what is my net cash flow'. " +
        "NEVER use budget allocations to answer those — an allocation is what the user PLANNED, not what happened. " +
        "Credit-card payments and transfers between the user's own accounts are excluded from both income " +
        "and spending: they are the same money moving, and counting them inflates both sides. " +
        "Refunds reduce spending rather than counting as income.")]
    public async Task<string> GetCashFlowAsync(
        [Description("User ID to get cash flow for")] int userId,
        [Description("Start of the period, inclusive (yyyy-MM-dd). Defaults to the start of the current month.")] string? startDate = null,
        [Description("End of the period, inclusive (yyyy-MM-dd). Defaults to today.")] string? endDate = null)
    {
        try
        {
            var now = DateTime.UtcNow;
            var start = ParseOrDefault(startDate, new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            // Inclusive end for the caller, exclusive internally, so "today" includes today.
            var endExclusive = ParseOrDefault(endDate, now.Date).AddDays(1);

            var user = await _userRepository.GetByIdAsync(userId);
            var transactions = await _transactionRepository.GetByUserOrFamilyAsync(
                userId, user?.FamilyId, start, endExclusive);

            var summary = CashFlowCalculator.Summarize(transactions, start, endExclusive);

            var result = new
            {
                Period = new
                {
                    Start = summary.PeriodStart.ToString("yyyy-MM-dd"),
                    End = summary.PeriodEndExclusive.AddDays(-1).ToString("yyyy-MM-dd"),
                },
                IncomeReceived = Math.Round(summary.IncomeReceived, 2),
                MoneySpent = Math.Round(summary.MoneySpent, 2),
                NetCashFlow = Math.Round(summary.NetCashFlow, 2),
                summary.TransactionsCounted,
                Excluded = new
                {
                    InternalMovement = Math.Round(summary.InternalMovementExcluded, 2),
                    Note = "Credit-card payments and transfers between the user's own accounts. " +
                           "Not income and not spending — the same money moving between their accounts.",
                },
                RefundsNetted = Math.Round(summary.RefundsNetted, 2),
                Guidance = "These are ACTUALS. Do not mix them with budget allocations from get_budgets, " +
                           "which are targets the user set, not money that moved. If the user has not yet " +
                           "received all their expected income for the period, say so rather than assuming " +
                           "the budgeted amount arrived.",
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_cash_flow for user {UserId}", userId);
            return JsonSerializer.Serialize(new { Error = "Could not calculate cash flow." });
        }
    }

    private static DateTime ParseOrDefault(string? value, DateTime fallback) =>
        DateTime.TryParse(value, out var parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc)
            : fallback;
}
