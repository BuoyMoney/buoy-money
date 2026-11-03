using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.Services;
using System.Diagnostics;

using BuoyAI.API.Attributes;

namespace BuoyAI.API.Controllers;

[Authorize]
[EnableRateLimiting("ai")]
public class AIController : BaseController
{
  private readonly IAIService _aiService;
  private readonly IAICategorizationService _categorizationService;
  private readonly IMcpToolExecutor _toolExecutor;
  private readonly IUserRepository _userRepository;
  private readonly IAiToolSecurityService _securityService;
  private readonly IUploadQuotaService _uploadQuota;
  private readonly IInsightActionService _insightActionService;
  private readonly IEntitlementService _entitlements;
  private readonly IConfiguration _configuration;
  private readonly ILogger<AIController> _logger;

  public AIController(
    IAIService aiService,
    IAICategorizationService categorizationService,
    IMcpToolExecutor toolExecutor,
    IUserRepository userRepository,
    IAiToolSecurityService securityService,
    IUploadQuotaService uploadQuota,
    IInsightActionService insightActionService,
    IEntitlementService entitlements,
    IConfiguration configuration,
    ILogger<AIController> logger)
  {
    _aiService = aiService;
    _categorizationService = categorizationService;
    _toolExecutor = toolExecutor;
    _userRepository = userRepository;
    _securityService = securityService;
    _uploadQuota = uploadQuota;
    _insightActionService = insightActionService;
    _entitlements = entitlements;
    _configuration = configuration;
    _logger = logger;
  }

  /// <summary>
  /// AI chat (query/chat/stream) is a Premium/Admin feature (family members on PremiumInherited
  /// included); Standard users get insights only.
  /// Enforced server-side so the paywall doesn't rely on the client hiding the UI (a Standard token
  /// could otherwise call these endpoints directly). Data is still user-scoped either way.
  /// </summary>
  /// <summary>Error code on every chat tier refusal so the app can offer an upgrade instead of a retry.</summary>
  public const string PremiumRequiredCode = "PremiumRequired";
  private const string PremiumRequiredMessage = "AI chat is part of Premium. Upgrade to Premium to use it.";

  private async Task<bool> IsChatTierAsync(int userId)
  {
    return await _entitlements.HasPremiumAccessAsync(userId);
  }

  [HttpPost("query")]
  [RequiresAiConsent]
  public async Task<IActionResult> ProcessQuery([FromBody] AIQueryRequest request)
  {
    request.ClientType = Request.Headers[RequiresAiConsentAttribute.ClientTypeHeader].ToString();
    if (string.IsNullOrEmpty(request.Query))
    {
      return BadRequest(new { message = "Query is required" });
    }

    var userId = GetCurrentUserId();
    if (!await IsChatTierAsync(userId))
      return StatusCode(403, new { error = PremiumRequiredCode, message = PremiumRequiredMessage });
    var response = await _aiService.ProcessQueryAsync(userId, request);

    return Ok(response);
  }

  /// <summary>
  /// Execute an MCP tool directly (used for tool confirmations)
  /// </summary>
  [HttpPost("tools/execute")]
  [RequiresAiConsent]
  public async Task<IActionResult> ExecuteTool([FromBody] ExecuteToolRequest request)
  {
    var stopwatch = Stopwatch.StartNew();
    var userId = GetCurrentUserId();
    string? errorMessage = null;
    string? result = null;

    try
    {
      if (string.IsNullOrEmpty(request.ToolName))
      {
        return BadRequest(new { message = "Tool name is required" });
      }

      // The token issued with the preview binds this confirm to this user, this tool and the
      // arguments the user saw (stored server-side); it works once and expires.
      int? confirmationId = null;
      if (request.ConfirmationToken.HasValue)
      {
        var confirmation = await _securityService.ValidateAndConsumeTokenAsync(
          userId, request.ConfirmationToken.Value);

        if (confirmation == null)
        {
          return BadRequest(new {
            message = "Invalid or expired confirmation token. Please try the action again.",
            code = "INVALID_TOKEN"
          });
        }

        if (!string.Equals(confirmation.ToolName, request.ToolName, StringComparison.Ordinal))
        {
          _logger.LogWarning(
            "Confirmation token for {TokenTool} was sent with {RequestTool} by user {UserId}",
            confirmation.ToolName, request.ToolName, userId);
          return BadRequest(new {
            message = "This confirmation doesn't match the action. Please try the action again.",
            code = "TOKEN_MISMATCH"
          });
        }

        // Run exactly what was previewed: the stored arguments, not the request's.
        confirmationId = confirmation.Id;
        request.Arguments = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
          confirmation.ArgumentsJson) ?? new Dictionary<string, object>();
      }
      else if (_configuration.GetValue("Ai:RequireConfirmationToken", false))
      {
        return BadRequest(new {
          message = "Please update the app to confirm this action.",
          code = "CONFIRMATION_TOKEN_REQUIRED"
        });
      }
      else
      {
        // App versions before confirmation tokens confirm without one. Still a user's own
        // tap on their own data; turn on Ai:RequireConfirmationToken once those versions age out.
        _logger.LogInformation("Tool {ToolName} confirmed without a token by user {UserId} (older app)", request.ToolName, userId);
      }

      var arguments = request.Arguments ?? new Dictionary<string, object>();
      result = await _toolExecutor.ExecuteConfirmedToolAsync(userId, request.ToolName, arguments);
      stopwatch.Stop();
      if (confirmationId.HasValue)
      {
        await _securityService.UpdateConfirmationResultAsync(confirmationId.Value, result ?? string.Empty, success: true);
      }

      // Log the execution
      await _securityService.LogToolExecutionAsync(
        userId,
        request.ToolName,
        arguments,
        isReadOnly: false, // Write operations go through this endpoint
        requiredConfirmation: request.ConfirmationToken.HasValue,
        confirmationToken: request.ConfirmationToken,
        result: result,
        success: true,
        errorMessage: null,
        executionTimeMs: (int)stopwatch.ElapsedMilliseconds,
        sessionId: null,
        ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
        userAgent: Request.Headers.UserAgent.ToString());
      
      return Ok(new { result, success = true });
    }
    catch (Exception ex)
    {
      stopwatch.Stop();
      errorMessage = ex.Message;
      _logger.LogError(ex, "Error executing tool {ToolName} for user {UserId}", request.ToolName, userId);

      // Log failed execution
      await _securityService.LogToolExecutionAsync(
        userId,
        request.ToolName,
        request.Arguments,
        isReadOnly: false,
        requiredConfirmation: request.ConfirmationToken.HasValue,
        confirmationToken: request.ConfirmationToken,
        result: null,
        success: false,
        errorMessage: ex.Message,
        executionTimeMs: (int)stopwatch.ElapsedMilliseconds,
        sessionId: null,
        ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
        userAgent: Request.Headers.UserAgent.ToString());

      return StatusCode(500, new { message = "An error occurred while executing the tool" });
    }
  }

  /// <summary>
  /// Get available MCP tools
  /// </summary>
  [HttpGet("tools")]
  public IActionResult GetAvailableTools()
  {
    var tools = _toolExecutor.GetAvailableTools();
    return Ok(tools);
  }

  /// <summary>
  /// Get user's AI interaction mode preference
  /// </summary>
  [HttpGet("preferences")]
  public async Task<IActionResult> GetAIPreferences()
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);
    
    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }
    
    return Ok(new {
      aiInteractionMode = user.AiInteractionMode.ToString(),
      aiInteractionModeValue = (int)user.AiInteractionMode,
      aiAutonomyLevel = user.AiAutonomyLevel.ToString(),
      aiAutonomyLevelValue = (int)user.AiAutonomyLevel
    });
  }

  /// <summary>
  /// Update user's AI interaction mode preference
  /// </summary>
  [HttpPut("preferences")]
  public async Task<IActionResult> UpdateAIPreferences([FromBody] UpdateAIPreferencesRequest request)
  {
    var userId = GetCurrentUserId();
    var user = await _userRepository.GetByIdAsync(userId);

    if (user == null)
    {
      return NotFound(new { message = "User not found" });
    }

    if (Enum.TryParse<AIInteractionMode>(request.AiInteractionMode, true, out var mode))
    {
      user.AiInteractionMode = mode;
      await _userRepository.UpdateAsync(user);

      return Ok(new {
        success = true,
        aiInteractionMode = user.AiInteractionMode.ToString()
      });
    }

    return BadRequest(new { message = "Invalid AI interaction mode. Valid values: Quick, Clarifying" });
  }

  [HttpPost("insights")]
  [RequiresAiConsent]
  public async Task<IActionResult> GenerateInsights([FromBody] AIInsightRequest request)
  {
    var userId = GetCurrentUserId();

    // AI Control Spectrum (#412): in Manual mode the AI stays silent — no proactive
    // insights. Higher levels (Coach+) surface them.
    if (await IsAiAutonomyManualAsync(userId))
    {
      return Ok(new { insights = new List<AIInsightDto>(), success = true, aiDisabled = true });
    }

    // Non-blocking: return cached insights immediately; on a cache miss, generation is
    // queued and runs off-thread so a slow Gemini call doesn't block this response (it
    // was pushing api/AI/insights P95 over the latency alert). Empty on a cold cache;
    // the insights appear on a later fetch — the client already handles an empty list.
    var insights = await _aiService.GetCachedOrEnqueueInsightsAsync(userId, request);

    return Ok(new { insights, success = true });
  }

  [HttpPost("insights/specific")]
  [RequiresAiConsent]
  public async Task<IActionResult> GenerateSpecificInsight([FromBody] AIInsightRequest request)
  {
    var userId = GetCurrentUserId();

    if (await IsAiAutonomyManualAsync(userId))
    {
      return Ok(new { insight = (AIInsightDto?)null, success = true, aiDisabled = true });
    }

    var insight = await _aiService.GenerateSpecificInsightAsync(userId, request);

    return Ok(new { insight, success = true });
  }

  /// <summary>
  /// True when the user has set their AI Control Spectrum to Manual (AI stays silent
  /// outside of explicit chat). Used to gate proactive AI surfaces.
  /// </summary>
  private async Task<bool> IsAiAutonomyManualAsync(int userId)
  {
    var user = await _userRepository.GetByIdAsync(userId);
    return user?.AiAutonomyLevel == AIAutonomyLevel.Manual;
  }

  [HttpPost("insights/{insightId}/dismiss")]
  public async Task<IActionResult> DismissInsight(string insightId)
  {
    var userId = GetCurrentUserId();
    var success = await _aiService.DismissInsightAsync(userId, insightId);

    return Ok(new { success });
  }

  /// <summary>
  /// Runs an insight's "agent_handoff" SuggestedAction. Tapping the button IS the user's
  /// confirmation, so the follow-up agent's (reversible, metadata-only) writes execute directly;
  /// the response carries a signed undo token for the follow-up "Undo" affordance.
  /// </summary>
  [HttpPost("insights/act")]
  [RequiresAiConsent] // runs a Gemini agent over the user's data
  public async Task<IActionResult> ActOnInsight([FromBody] InsightActRequest request)
  {
    if (request == null || string.IsNullOrWhiteSpace(request.Instruction))
    {
      return BadRequest(new { message = "Instruction is required" });
    }

    var userId = GetCurrentUserId();
    var result = await _insightActionService.ExecuteHandoffAsync(userId, request.Instruction);

    return Ok(new
    {
      success = result.Success,
      summary = result.Summary,
      changes = result.Changes,
      undoToken = result.UndoToken,
      canUndo = result.CanUndo
    });
  }

  /// <summary>Reverts the changes made by a prior insights/act call, using its signed undo token.</summary>
  [HttpPost("insights/undo")]
  public async Task<IActionResult> UndoInsightAction([FromBody] InsightUndoRequest request)
  {
    if (request == null || string.IsNullOrWhiteSpace(request.UndoToken))
    {
      return BadRequest(new { message = "Undo token is required" });
    }

    var userId = GetCurrentUserId();
    var result = await _insightActionService.UndoAsync(userId, request.UndoToken);

    return Ok(new { success = result.Success, summary = result.Summary });
  }

  [HttpPost("insights/generate/{context}")]
  [RequiresAiConsent]
  public async Task<IActionResult> TriggerInsightGeneration(string context)
  {
    var userId = GetCurrentUserId();
    // Trigger fresh insight generation by clearing cache and generating new insights
    var request = new AIInsightRequest { Context = context, MaxInsights = 5 };
    var insights = await _aiService.GenerateContextualInsightsAsync(userId, request);

    return Ok(new { insights, success = true });
  }

  [HttpPost("insights/refresh/{context}")]
  [RequiresAiConsent]
  public async Task<IActionResult> RefreshInsights(string context)
  {
    var userId = GetCurrentUserId();
    var insights = await _aiService.RefreshContextualInsightsAsync(userId, context, 5);
    var usageStats = await _aiService.GetUsageStatsAsync(userId);

    var refreshStats = usageStats.RefreshStats.GetValueOrDefault(context);

    return Ok(new {
      insights,
      success = true,
      refreshStats = refreshStats
    });
  }

  [HttpGet("usage")]
  public async Task<IActionResult> GetUsageStats()
  {
    var userId = GetCurrentUserId();
    var stats = await _aiService.GetUsageStatsAsync(userId);

    return Ok(new { stats, success = true });
  }

  [HttpPost("test-api-key")]
  public async Task<IActionResult> TestApiKey()
  {
    var userId = GetCurrentUserId();
    var testRequest = new AIQueryRequest
    {
      Query = "Hello, this is a test to verify the Gemini API key is working correctly.",
      Context = null
    };

    var response = await _aiService.ProcessQueryAsync(userId, testRequest);

    if (response.Success && !response.Response.Contains("mock") && !response.Response.Contains("Mock"))
    {
      return Ok(new {
        success = true,
        message = "Gemini API key is working correctly!",
        response = response.Response
      });
    }
    else
    {
      return Ok(new {
        success = false,
        message = "API key test failed or using mock responses",
        response = response.Response,
        error = response.Error
      });
    }
  }

  /// <summary>
  /// AI Chat endpoint for Pro users to discuss their financial picture
  /// Requires Premium or Admin subscription tier
  /// </summary>
  [HttpPost("chat")]
  [RequiresAiConsent]
  public async Task<IActionResult> Chat([FromBody] AIChatRequest request)
  {
    request.ClientType = Request.Headers[RequiresAiConsentAttribute.ClientTypeHeader].ToString();
    if (string.IsNullOrEmpty(request.Message))
    {
      return BadRequest(new { message = "Message is required" });
    }

    var userId = GetCurrentUserId();
    if (!await IsChatTierAsync(userId))
      return StatusCode(403, new { error = PremiumRequiredCode, message = PremiumRequiredMessage });

    // Carry what the streaming endpoint gets: the client type (a phone gets short, table-free
    // replies; without it every reply here was shaped for a wide screen) and the history.
    var queryRequest = new AIQueryRequest
    {
      Query = request.Message,
      Context = request.Context,
      ClientType = request.ClientType,
      ConversationHistory = request.ConversationHistory?
        .Select(m => new ConversationMessage { Role = m.Role, Content = m.Content })
        .ToList()
    };

    var response = await _aiService.ProcessQueryAsync(userId, queryRequest);

    return Ok(new
    {
      success = response.Success,
      message = response.Response,
      error = response.Error,
      conversationId = request.SessionId?.ToString() ?? Guid.NewGuid().ToString()
    });
  }

  /// <summary>
  /// AI Chat streaming endpoint for Pro users - streams responses in real-time with function calling
  /// Requires Premium or Admin subscription tier
  /// Streams SSE events: text, function_call, function_result, confirmation_required, done, error
  /// </summary>
  [HttpPost("chat/stream")]
  [RequiresAiConsent]
  public async Task ChatStream([FromBody] AIChatRequest request)
  {
    request.ClientType = Request.Headers[RequiresAiConsentAttribute.ClientTypeHeader].ToString();
    try
    {
      if (string.IsNullOrEmpty(request.Message))
      {
        Response.StatusCode = 400;
        await Response.WriteAsync("Message is required");
        return;
      }

      var userId = GetCurrentUserId();

      if (!await IsChatTierAsync(userId))
      {
        Response.StatusCode = 403;
        Response.ContentType = "application/json";
        await Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = PremiumRequiredCode, message = PremiumRequiredMessage }));
        return;
      }

      // Set up SSE headers
      Response.ContentType = "text/event-stream";
      Response.Headers.Append("Cache-Control", "no-cache");
      Response.Headers.Append("Connection", "keep-alive");
      Response.Headers.Append("X-Accel-Buffering", "no"); // Disable nginx buffering
      
      await Response.Body.FlushAsync();
      
      _logger.LogInformation("Starting chat stream with tools for user {UserId}", userId);
      
      var jsonOptions = new System.Text.Json.JsonSerializerOptions 
      { 
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase 
      };
      
      await foreach (var evt in _aiService.ProcessChatStreamWithToolsAsync(userId, request))
      {
        // Send as Server-Sent Events format with event type
        var data = $"event: {evt.Type}\ndata: {System.Text.Json.JsonSerializer.Serialize(evt, jsonOptions)}\n\n";
        await Response.WriteAsync(data);
        await Response.Body.FlushAsync();
      }
      
      _logger.LogInformation("Completed chat stream for user {UserId}", userId);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error streaming AI chat for user {UserId}", GetCurrentUserId());
      var errorEvt = new { type = "error", error = "An error occurred while streaming the response" };
      var errorData = $"event: error\ndata: {System.Text.Json.JsonSerializer.Serialize(errorEvt)}\n\n";
      await Response.WriteAsync(errorData);
      await Response.Body.FlushAsync();
    }
  }

  /// <summary>
  /// Upload a file for AI chat analysis. Files are uploaded to Gemini Files API and expire after 48 hours.
  /// Supported types: OFX, QFX, QIF, CSV, TXT, PDF (financial documents only - no images or video)
  /// Size limits: PDF max 10MB, others max 5MB (TXT max 2MB)
  /// </summary>
  [HttpPost("chat/upload")]
  [RequiresAiConsent]
  [RequestSizeLimit(10_000_000)] // 10MB max
  public async Task<IActionResult> UploadChatFile(IFormFile file)
  {
    if (file == null || file.Length == 0)
    {
      return BadRequest(new ChatFileUploadResponse { Success = false, Error = "No file provided" });
    }

    var userId = GetCurrentUserId();

    // Validate MIME type before processing
    var mimeType = file.ContentType?.ToLowerInvariant() ?? "";

    // Map common variations to standard MIME types
    mimeType = mimeType switch
    {
      "application/vnd.ms-excel" when file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) => "text/csv",
      "application/octet-stream" => GetMimeTypeFromExtension(file.FileName),
      "" => GetMimeTypeFromExtension(file.FileName),
      _ => mimeType
    };

    if (!AllowedChatFileTypes.IsAllowed(mimeType))
    {
      var allowedExtensions = string.Join(", ", AllowedChatFileTypes.MimeTypes.Keys);
      return BadRequest(new ChatFileUploadResponse
      {
        Success = false,
        Error = $"File type not allowed. Supported types: {allowedExtensions}"
      });
    }

    // Security (#367): verify the file's actual content matches its declared type. The MIME
    // type from the request is attacker-controlled — a renamed executable would otherwise pass.
    var header = new byte[FileSignatureValidator.HeaderSize];
    int read;
    using (var probe = file.OpenReadStream())
    {
      read = await probe.ReadAsync(header.AsMemory(0, header.Length));
    }
    if (!FileSignatureValidator.IsContentConsistent(header.AsSpan(0, read), mimeType))
    {
      _logger.LogWarning("User {UserId} upload rejected: content does not match declared type {MimeType} ({FileName})",
        userId, mimeType, file.FileName);
      return BadRequest(new ChatFileUploadResponse
      {
        Success = false,
        Error = "File content does not match its type. Please upload a valid financial document."
      });
    }

    // Enforce the per-user daily upload quota (#367).
    var remaining = _uploadQuota.GetRemainingBytesToday(userId);
    if (file.Length > remaining)
    {
      _logger.LogWarning("User {UserId} upload rejected: daily quota exceeded ({Size} > {Remaining} remaining)",
        userId, file.Length, remaining);
      return BadRequest(new ChatFileUploadResponse
      {
        Success = false,
        Error = $"Daily upload limit reached ({_uploadQuota.DailyQuotaBytes / (1024 * 1024)}MB/day). Try again tomorrow."
      });
    }

    _logger.LogInformation("User {UserId} uploading file {FileName} ({MimeType}, {Size} bytes)",
      userId, file.FileName, mimeType, file.Length);

    using var stream = file.OpenReadStream();
    var result = await _aiService.UploadChatFileAsync(userId, stream, file.FileName, mimeType);

    if (result.Success)
    {
      _uploadQuota.RecordUsage(userId, file.Length);
      _logger.LogInformation("File upload successful for user {UserId}: {FileUri}", userId, result.FileUri);
    }
    else
    {
      _logger.LogWarning("File upload failed for user {UserId}: {Error}", userId, result.Error);
    }

    return result.Success ? Ok(result) : BadRequest(result);
  }

  /// <summary>
  /// Advertise the allowed chat-upload file types + size limits so clients read them from a
  /// single backend source of truth instead of hardcoding (issue #367).
  /// </summary>
  [HttpGet("chat/file-types")]
  public IActionResult GetChatFileTypes()
  {
    return Ok(new
    {
      extensions = AllowedChatFileTypes.MimeTypes,                 // ".pdf" -> "application/pdf"
      maxSizeBytes = AllowedChatFileTypes.MaxSizeBytes,            // mime -> max bytes
      dailyQuotaBytes = _uploadQuota.DailyQuotaBytes
    });
  }

  private static string GetMimeTypeFromExtension(string fileName)
  {
    var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
    return extension switch
    {
      ".pdf" => "application/pdf",
      ".csv" => "text/csv",
      ".txt" => "text/plain",
      ".ofx" => "application/x-ofx",
      ".qfx" => "application/vnd.intu.qfx",
      ".qif" => "application/qif",
      _ => "application/octet-stream"
    };
  }

  // ==================== AI Transaction Categorization Endpoints ====================

  /// <summary>
  /// Categorize a single transaction using AI
  /// </summary>
  [HttpPost("categorize/{transactionId}")]
  [RequiresAiConsent]
  public async Task<IActionResult> CategorizeTransaction(int transactionId)
  {
    var userId = GetCurrentUserId();
    var result = await _categorizationService.CategorizeTransactionAsync(transactionId, userId);

    if (result.Error != null)
    {
      return BadRequest(new { message = result.Error });
    }

    return Ok(result);
  }

  /// <summary>
  /// Batch categorize transactions using AI
  /// </summary>
  [HttpPost("categorize/batch")]
  [RequiresAiConsent]
  public async Task<IActionResult> BatchCategorize([FromBody] AICategorizationRequest request)
  {
    var userId = GetCurrentUserId();
    var response = await _categorizationService.BatchCategorizeAsync(userId, request);
    return Ok(response);
  }

  /// <summary>
  /// Get statistics about transactions needing review
  /// </summary>
  [HttpGet("categorize/review-stats")]
  public async Task<IActionResult> GetReviewStatistics()
  {
    var userId = GetCurrentUserId();
    var stats = await _categorizationService.GetReviewStatisticsAsync(userId);
    return Ok(stats);
  }

  /// <summary>
  /// Get transactions needing review, sorted by AI confidence (highest first for quick review)
  /// </summary>
  // scope defaults to All to preserve the pre-existing behavior for any caller that omits it;
  // the Flutter review queue opts into ThisMonth / Older explicitly.
  [HttpGet("categorize/needs-review")]
  public async Task<IActionResult> GetTransactionsNeedingReview(
      [FromQuery] int? limit = null, [FromQuery] ReviewScope scope = ReviewScope.All)
  {
    var userId = GetCurrentUserId();
    var transactions = await _categorizationService.GetTransactionsNeedingReviewAsync(userId, limit, scope);
    return Ok(transactions);
  }

  /// <summary>
  /// Accept an AI-suggested category for a transaction
  /// </summary>
  [HttpPost("categorize/{transactionId}/accept")]
  [EnableRateLimiting("api")]
  public async Task<IActionResult> AcceptAISuggestion(int transactionId)
  {
    try
    {
      var userId = GetCurrentUserId();
      var result = await _categorizationService.AcceptAISuggestionAsync(transactionId, userId);
      return Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Reject an AI-suggested category and apply a different one
  /// </summary>
  [HttpPost("categorize/{transactionId}/reject")]
  [EnableRateLimiting("api")]
  public async Task<IActionResult> RejectAISuggestion(int transactionId, [FromBody] RejectSuggestionRequest request)
  {
    // A category to apply must be supplied; a missing/omitted body previously bound
    // to enum 0 (undefined) and 500'd on the FK. Require an explicit id.
    if (!request.CorrectCategory.HasValue)
    {
      return BadRequest(new { message = "A correct category id must be provided." });
    }

    try
    {
      var userId = GetCurrentUserId();
      var result = await _categorizationService.RejectAISuggestionAsync(transactionId, userId, request.CorrectCategory.Value);
      return Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Skip a transaction from review for a specified number of days (default: 2)
  /// </summary>
  [HttpPost("categorize/{transactionId}/skip")]
  [EnableRateLimiting("api")]
  public async Task<IActionResult> SkipTransaction(int transactionId, [FromQuery] int days = 2)
  {
    try
    {
      var userId = GetCurrentUserId();
      var result = await _categorizationService.SkipTransactionReviewAsync(transactionId, userId, days);
      return Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
      return NotFound(new { message = ex.Message });
    }
  }

  /// <summary>
  /// Permanently dismisses every transaction older than the current month that is still
  /// waiting for review — the "forget the backlog" action. Returns how many were dismissed.
  ///
  /// The cutoff is fixed server-side rather than accepted from the client, so this can only
  /// ever clear the same set the banner reports as "from earlier months".
  /// </summary>
  [HttpPost("categorize/dismiss-older")]
  [EnableRateLimiting("api")]
  public async Task<IActionResult> DismissOlderReviews()
  {
    var userId = GetCurrentUserId();
    var dismissed = await _categorizationService.DismissOlderReviewsAsync(userId);
    return Ok(new { dismissed });
  }

  /// <summary>
  /// Get the count of transactions queued for AI categorization
  /// </summary>
  [HttpGet("categorize/queue/count")]
  public async Task<IActionResult> GetQueuedCount()
  {
    var userId = GetCurrentUserId();
    var count = await _categorizationService.GetQueuedTransactionCountAsync(userId);
    return Ok(new { count });
  }

  /// <summary>
  /// Process the AI categorization queue for the current user.
  /// This is called after transaction sync to process queued items in batches.
  /// </summary>
  [HttpPost("categorize/queue/process")]
  [RequiresAiConsent] // sends transaction details to Gemini; the background drainer checks the same rule
  public async Task<IActionResult> ProcessQueue([FromQuery] int batchSize = 20)
  {
    var userId = GetCurrentUserId();

    // Cap batch size to prevent abuse
    if (batchSize > 50) batchSize = 50;
    if (batchSize < 1) batchSize = 1;

    var processedCount = await _categorizationService.ProcessCategorizationQueueAsync(userId, batchSize);
    var remainingCount = await _categorizationService.GetQueuedTransactionCountAsync(userId);

    return Ok(new {
      processed = processedCount,
      remaining = remainingCount,
      hasMore = remainingCount > 0
    });
  }
}

public class RejectSuggestionRequest
{
  /// <summary>
  /// Unified CategoryId to apply instead of the AI suggestion. System categories
  /// are 1-99; user-created custom categories are 1000+. Nullable so an omitted
  /// value is rejected explicitly rather than silently binding to enum 0.
  /// Wire name stays "correctCategory" for compatibility with the shipped app.
  /// </summary>
  public int? CorrectCategory { get; set; }
}
