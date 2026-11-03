using System.Security.Cryptography;
using BuoyAI.Infrastructure.Services.Ai;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.MCP;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Runs an insight's "agent_handoff" SuggestedAction: a bounded, write-capable agent that carries
/// out exactly the requested change. The user's button tap is the confirmation (so writes run with
/// confirmed=true), and every change is journaled as an inverse operation so it can be undone.
///
/// Safety posture:
///  - The agent is only given READ tools plus a small set of CLEANLY-REVERSIBLE, metadata-only
///    write tools (<see cref="ReversibleWriteTools"/>). Money-movement writes are deliberately not
///    offered here — their undo can't be made faithful behind a one-tap confirm.
///  - Family write-permission is enforced up front AND by each tool (button tap is not a
///    permission bypass).
///  - Undo is stateless: the response carries an HMAC-signed journal (no new table), which the undo
///    endpoint verifies before replaying the inverse operations in reverse order.
/// </summary>
public interface IInsightActionService
{
    Task<InsightActResponse> ExecuteHandoffAsync(int userId, string instruction, CancellationToken cancellationToken = default);
    Task<InsightUndoResponse> UndoAsync(int userId, string undoToken, CancellationToken cancellationToken = default);
}

public class InsightActionService : IInsightActionService
{
    // The ONLY write the one-tap handoff agent may perform. categorize_transaction is the single
    // operation whose inverse is truly faithful (restore the prior CategoryId — see ApplyReversal).
    // Deliberately excluded: update_budget_amount (its service call overwrites AllowRollover /
    // rollover config to defaults) and the earmark tools (they move real money in/out of a savings
    // goal, so re-linking the FK does not reverse the balance change). Those remain available in
    // normal chat with their own per-tool confirmation; they are not safe behind a one-tap confirm.
    internal static readonly IReadOnlyList<string> ReversibleWriteTools = new[]
    {
        "categorize_transaction",
    };

    private const int HandoffMaxIterations = 6;

    // An undo token is a short-lived affordance tied to the snackbar after a handoff; expire it so
    // it can't be replayed much later to revert an unrelated subsequent change.
    private const long MaxUndoAgeSeconds = 24 * 60 * 60;

    private const string WriteAgentSystemPrompt =
        "You are carrying out a specific change the user has ALREADY approved by tapping a button. " +
        "Use the provided tools to perform EXACTLY the requested task and nothing more. You may use " +
        "the read tools to look up IDs first. For write tools, the user's tap is the confirmation, so " +
        "pass confirmed=true. Make the minimal set of changes needed. Never reclassify a transaction as " +
        "a transfer unless the instruction explicitly says so. When finished, reply with ONE or TWO " +
        "plain-English sentences summarizing what you changed (or that nothing needed changing). Do not " +
        "ask questions.";

    private readonly IGeminiAgenticRunner _agenticRunner;
    private readonly IMcpToolExecutor _toolExecutor;
    private readonly ITransactionService _transactionService;
    private readonly IFamilyService _familyService;
    private readonly AIModelConfiguration _modelConfiguration;
    private readonly ILogger<InsightActionService> _logger;
    private readonly byte[] _signingKey;

    public InsightActionService(
        IGeminiAgenticRunner agenticRunner,
        IMcpToolExecutor toolExecutor,
        ITransactionService transactionService,
        IFamilyService familyService,
        AIModelConfiguration modelConfiguration,
        IConfiguration configuration,
        ILogger<InsightActionService> logger)
    {
        _agenticRunner = agenticRunner;
        _toolExecutor = toolExecutor;
        _transactionService = transactionService;
        _familyService = familyService;
        _modelConfiguration = modelConfiguration;
        _logger = logger;
        _signingKey = Encoding.UTF8.GetBytes(
            configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key not configured"));
    }

    public async Task<InsightActResponse> ExecuteHandoffAsync(int userId, string instruction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return new InsightActResponse { Success = false, Summary = "No action was provided." };
        }

        if (!await _familyService.CanUserModifyFinancialDataAsync(userId))
        {
            return new InsightActResponse
            {
                Success = false,
                Summary = "You have view-only access to this account, so I can't make changes on your behalf."
            };
        }

        // Read tools (to find IDs) + the reversible write subset only.
        var readNames = _toolExecutor.GetAvailableTools().Where(t => t.ReadOnly).Select(t => t.Name);
        var allowed = new HashSet<string>(readNames);
        foreach (var w in ReversibleWriteTools)
        {
            allowed.Add(w);
        }

        var declarations = _toolExecutor.GetGeminiFunctionDeclarations(readOnlyOnly: false, allowedToolNames: allowed);

        var ops = new List<ReversalOp>();

        Func<string, Dictionary<string, object>, Task<string>> execute = async (toolName, args) =>
        {
            // Hard gate: never execute a write outside the reversible subset, even if somehow requested.
            var isReversibleWrite = ReversibleWriteTools.Contains(toolName);
            var isRead = allowed.Contains(toolName) && !isReversibleWrite;
            if (!isRead && !isReversibleWrite)
            {
                return JsonSerializer.Serialize(new { Error = $"Tool '{toolName}' is not permitted in this action." });
            }

            if (!isReversibleWrite)
            {
                return await _toolExecutor.ExecuteToolAsync(userId, toolName, args, cancellationToken);
            }

            // Capture the inverse BEFORE mutating, then execute with the tap as confirmation.
            var inverse = await CaptureInverseAsync(userId, toolName, args);
            var result = await _toolExecutor.ExecuteConfirmedToolAsync(userId, toolName, args, cancellationToken);

            if (inverse != null && IndicatesSuccess(result))
            {
                ops.Add(inverse);
            }

            return result;
        };

        var model = _modelConfiguration.GetPrimaryModel(SubscriptionTier.Premium);
        using var usageScope = AiUsageScope.ForUser(userId);
        var run = await _agenticRunner.RunAsync(
            model, WriteAgentSystemPrompt, instruction, declarations, execute, HandoffMaxIterations, cancellationToken);

        var summary = string.IsNullOrWhiteSpace(run.FinalText)
            ? (ops.Count > 0 ? "Done." : "I couldn't complete that action.")
            : run.FinalText.Trim();

        _logger.LogInformation(
            "Insight handoff for user {UserId} made {Changes} reversible change(s) via {ToolCalls} tool call(s).",
            userId, ops.Count, run.ToolCalls.Count);

        return new InsightActResponse
        {
            Success = ops.Count > 0 || !string.IsNullOrWhiteSpace(run.FinalText),
            Summary = summary,
            Changes = ops.Select(o => new InsightActionChange { Description = o.Description }).ToList(),
            UndoToken = ops.Count > 0
                ? SignToken(new UndoPayload { UserId = userId, IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Ops = ops })
                : null
        };
    }

    public async Task<InsightUndoResponse> UndoAsync(int userId, string undoToken, CancellationToken cancellationToken = default)
    {
        if (!TryVerifyToken(undoToken, out var payload) || payload == null)
        {
            return new InsightUndoResponse { Success = false, Summary = "This undo is no longer valid." };
        }

        // The token is bound to the user who made the change; never let it act for anyone else.
        if (payload.UserId != userId)
        {
            _logger.LogWarning("Undo token user mismatch: token={TokenUser} caller={Caller}", payload.UserId, userId);
            return new InsightUndoResponse { Success = false, Summary = "This undo doesn't belong to your account." };
        }

        if (!await _familyService.CanUserModifyFinancialDataAsync(userId))
        {
            return new InsightUndoResponse { Success = false, Summary = "You no longer have permission to change this data." };
        }

        var undone = 0;
        // Reverse order so chained edits on the same entity restore the original state.
        for (var i = payload.Ops.Count - 1; i >= 0; i--)
        {
            try
            {
                await ApplyReversalAsync(userId, payload.Ops[i]);
                undone++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply reversal op {Kind} for user {UserId}", payload.Ops[i].Kind, userId);
            }
        }

        return new InsightUndoResponse
        {
            Success = undone > 0,
            Summary = undone == payload.Ops.Count
                ? "Reverted."
                : undone == 0 ? "Couldn't undo those changes." : $"Reverted {undone} of {payload.Ops.Count} changes."
        };
    }

    /// <summary>Reads current state and builds the inverse op for a reversible write. Null if uncapturable.</summary>
    private async Task<ReversalOp?> CaptureInverseAsync(int userId, string toolName, Dictionary<string, object> args)
    {
        try
        {
            switch (toolName)
            {
                case "categorize_transaction":
                {
                    var txId = GetInt(args, "transactionId");
                    if (txId == null) return null;
                    var tx = await _transactionService.GetTransactionByIdAsync(txId.Value, userId);
                    if (tx == null) return null;
                    var prior = tx.CategoryId ?? (int)TransactionCategory.Uncategorized;
                    return new ReversalOp
                    {
                        Kind = ReversalKind.SetTransactionCategory,
                        TransactionId = txId.Value,
                        CategoryId = prior,
                        Description = $"Category of \"{Describe(tx)}\" back to {tx.CategoryName}"
                    };
                }

                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not capture inverse for {ToolName}", toolName);
            return null;
        }
    }

    private async Task ApplyReversalAsync(int userId, ReversalOp op)
    {
        switch (op.Kind)
        {
            case ReversalKind.SetTransactionCategory:
                await _transactionService.UpdateTransactionCategoryAsync(
                    op.TransactionId!.Value,
                    new UpdateTransactionCategoryRequest { CategoryId = op.CategoryId!.Value },
                    userId);
                break;
        }
    }

    /// <summary>
    /// Whether a tool result reports an actual mutation. Parses the JSON and inspects TOP-LEVEL
    /// properties (rather than substring-sniffing, which could false-positive on a nested string
    /// value or a merchant literally named "Error"). A change is only journaled for undo when the
    /// forward write truly succeeded.
    /// </summary>
    private static bool IndicatesSuccess(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult)) return false;

        try
        {
            using var doc = JsonDocument.Parse(toolResult);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;

            // A confirmation preview or an error means nothing was actually changed.
            if (root.TryGetProperty("Error", out _)) return false;
            if (root.TryGetProperty("RequiresConfirmation", out var rc) &&
                rc.ValueKind == JsonValueKind.True)
            {
                return false;
            }

            return root.TryGetProperty("Success", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false; // unparseable result -> treat as not-a-successful-mutation
        }
    }

    private static string Describe(TransactionDto tx)
        => string.IsNullOrWhiteSpace(tx.MerchantName) ? tx.Description : tx.MerchantName;

    private static int? GetInt(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value == null) return null;
        switch (value)
        {
            case int i: return i;
            case long l: return (int)l;
            case JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n): return n;
            case JsonElement je when je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var s): return s;
            case string str when int.TryParse(str, out var p): return p;
            default: return null;
        }
    }

    // ---- Signed, stateless undo token ----

    internal string SignToken(UndoPayload payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var payloadB64 = Base64Url(Encoding.UTF8.GetBytes(json));
        var sig = Base64Url(HmacSha256(payloadB64));
        return $"{payloadB64}.{sig}";
    }

    internal bool TryVerifyToken(string token, out UndoPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 2) return false;

        var expected = Base64Url(HmacSha256(parts[0]));
        // Constant-time comparison to avoid signature timing oracles.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1])))
        {
            return false;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            payload = JsonSerializer.Deserialize<UndoPayload>(json);
            if (payload == null || payload.Ops.Count == 0)
            {
                return false;
            }

            // Expire the token so it can't be replayed indefinitely to clobber a later change.
            var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - payload.IssuedAtUnix;
            if (ageSeconds < 0 || ageSeconds > MaxUndoAgeSeconds)
            {
                payload = null;
                return false;
            }

            return true;
        }
        catch
        {
            payload = null;
            return false;
        }
    }

    private byte[] HmacSha256(string data)
    {
        using var hmac = new HMACSHA256(_signingKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    internal enum ReversalKind
    {
        SetTransactionCategory
    }

    internal class ReversalOp
    {
        public ReversalKind Kind { get; set; }
        public int? TransactionId { get; set; }
        public int? CategoryId { get; set; }
        public string Description { get; set; } = string.Empty;
    }

    internal class UndoPayload
    {
        public int UserId { get; set; }
        public long IssuedAtUnix { get; set; }
        public List<ReversalOp> Ops { get; set; } = new();
    }
}
