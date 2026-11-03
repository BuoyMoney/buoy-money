using BuoyAI.API.Services;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Net.Http.Headers;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Full data export (portability). Free for every plan: it is not gated on a subscription tier.
/// The file is streamed straight to the response, table by table.
/// </summary>
[Authorize]
[Route("api/export")]
[EnableRateLimiting("export")]
public class ExportController : BaseController
{
    private const ProtectedOperation StepUpOperation = ProtectedOperation.ViewSensitiveFinancialData;

    /// <summary>Hard deadline for one export, so a stalled reader cannot hold a slot forever.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private readonly IUserDataExportService _exportService;
    private readonly IStepUpAuthService _stepUp;
    private readonly IEmailService _email;
    private readonly ExportConcurrencyGate _gate;
    private readonly ILogger<ExportController> _logger;

    public ExportController(
        IUserDataExportService exportService,
        IStepUpAuthService stepUp,
        IEmailService email,
        ExportConcurrencyGate gate,
        ILogger<ExportController> logger)
    {
        _exportService = exportService;
        _stepUp = stepUp;
        _email = email;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>
    /// Downloads everything the caller owns plus the family data they can already see.
    /// format=json gives one JSON document; format=csv gives a zip with one CSV per table.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Export([FromQuery] string? format, CancellationToken cancellationToken)
    {
        var normalized = (format ?? "json").Trim().ToLowerInvariant();
        if (normalized != "json" && normalized != "csv")
        {
            return BadRequest("format must be 'json' or 'csv'");
        }

        var userId = GetCurrentUserId();
        var scope = await _exportService.ResolveScopeAsync(userId, cancellationToken);
        if (scope == null)
        {
            return NotFound();
        }

        // Same step-up the app uses for other sensitive actions, for accounts that have a second
        // factor (MFA flag or a registered passkey). Accounts with none have no way to step up and
        // portability must stay available to them; they get an email notice after each export.
        if (scope.HasMfa)
        {
            var token = Request.Headers["X-Operation-Token"].FirstOrDefault();
            var tokenOk = !string.IsNullOrEmpty(token)
                && await _stepUp.ValidateOperationTokenAsync(token, StepUpOperation, userId);
            if (!tokenOk && await _stepUp.RequiresMfaForOperationAsync(userId, StepUpOperation))
            {
                return new ObjectResult(new
                {
                    error = "MfaRequired",
                    message = "This operation requires multi-factor authentication",
                    operation = StepUpOperation.ToString(),
                    requirementLevel = _stepUp.GetRequirementLevel(StepUpOperation).ToString(),
                })
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                };
            }
        }

        // At most one export per user and two on the server.
        using var lease = _gate.TryEnter(userId);
        if (lease == null)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "An export is already running. Try again in a minute.", code = "export_in_progress" });
        }

        await _exportService.RecordExportAsync(userId, normalized, cancellationToken);

        var isJson = normalized == "json";
        var fileName = $"buoy-export-{DateTime.UtcNow:yyyy-MM-dd}.{(isJson ? "json" : "zip")}";

        Response.ContentType = isJson ? "application/json" : "application/zip";
        Response.Headers.CacheControl = "no-store";
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = fileName,
        }.ToString();

        // ZipArchive flushes and disposes synchronously. Bounded by the gate (2 total, 1 per user).
        var bodyControl = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (bodyControl != null)
        {
            bodyControl.AllowSynchronousIO = true;
        }
        // A client that reads slower than this is cut off instead of holding a slot.
        var dataRate = HttpContext.Features.Get<IHttpMinResponseDataRateFeature>();
        if (dataRate != null)
        {
            dataRate.MinDataRate = new MinDataRate(bytesPerSecond: 1024, gracePeriod: TimeSpan.FromSeconds(30));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        _logger.LogInformation("Data export ({Format}) started for user {UserId}", normalized, userId);
        try
        {
            if (isJson)
            {
                await _exportService.WriteJsonAsync(scope, Response.Body, deadline.Token);
            }
            else
            {
                await _exportService.WriteCsvZipAsync(scope, Response.Body, deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away, or the deadline hit. Expected; not an error worth a Sentry event.
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Data export ({Format}) for user {UserId} cancelled by the client", normalized, userId);
            }
            else
            {
                _logger.LogWarning("Data export ({Format}) for user {UserId} hit the {Minutes}-minute deadline", normalized, userId, Deadline.TotalMinutes);
            }
            HttpContext.Abort();
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            // Headers are already sent, so the status can no longer change. Cut the connection so
            // the client sees a failed download instead of a truncated file that looks complete.
            _logger.LogError(ex, "Data export ({Format}) failed mid-stream for user {UserId}", normalized, userId);
            HttpContext.Abort();
            return new EmptyResult();
        }

        // Give the slot back before anything slow: a second export right after this one must not
        // be refused because the notice email is still being sent. Dispose is idempotent.
        lease.Dispose();

        // Finish the response first so the notice email never delays the download.
        await Response.CompleteAsync();
        await NotifyOwnerAsync(scope.User.Email, scope.User.FirstName, normalized);
        return new EmptyResult();
    }

    private async Task NotifyOwnerAsync(string email, string firstName, string format)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }
        try
        {
            await _email.SendDataExportedAsync(email, string.IsNullOrWhiteSpace(firstName) ? "there" : firstName, format, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // Self-host installs may have no email provider. Never fail an export over it.
            _logger.LogWarning(ex, "Could not send the data export notice");
        }
    }
}
