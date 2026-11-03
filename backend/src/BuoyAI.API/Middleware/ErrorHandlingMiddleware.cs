using System.Net;
using System.Security.Claims;
using System.Text.Json;
using BuoyAI.Core.Exceptions;
using Sentry;

namespace BuoyAI.API.Middleware;

public class ErrorHandlingMiddleware
{
  private readonly RequestDelegate _next;
  private readonly ILogger<ErrorHandlingMiddleware> _logger;
  private readonly IHostEnvironment _environment;

  public ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger, IHostEnvironment environment)
  {
    _next = next;
    _logger = logger;
    _environment = environment;
  }

  public async Task InvokeAsync(HttpContext context)
  {
    try
    {
      await _next(context);
    }
    catch (AiAllowanceException ex)
    {
      // An expected answer ("your AI allowance is used up"), not a fault: no error log, no Sentry event.
      _logger.LogInformation("AI allowance refused a request ({Code}). TraceId: {TraceId}", ex.Code, context.TraceIdentifier);
      await HandleExceptionAsync(context, ex);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "An unhandled exception occurred. TraceId: {TraceId}", context.TraceIdentifier);
      
      // Explicitly capture to Sentry since we're handling the exception
      // This ensures errors are reported even when the middleware catches them
      SentrySdk.CaptureException(ex, scope =>
      {
        scope.SetTag("traceId", context.TraceIdentifier);
        // Link-session paths carry a live pairing code; tag the redacted form.
        scope.SetTag("path", BuoyAI.Core.Utilities.SecretPathRedaction.Redact(context.Request.Path.Value) ?? "unknown");
        scope.SetTag("method", context.Request.Method);

        // Attribute the error to the authenticated caller so backend crashes are
        // per-user in Sentry. Only the user id is set (sendDefaultPii stays false) — no
        // email/IP/PII is attached.
        var userId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? context.User?.FindFirst("nameid")?.Value;
        if (!string.IsNullOrEmpty(userId))
        {
          scope.User = new SentryUser { Id = userId };
        }
      });
      
      await HandleExceptionAsync(context, ex);
    }
  }

  private async Task HandleExceptionAsync(HttpContext context, Exception exception)
  {
    // If the response has already begun streaming we can't rewrite status/body — writing
    // now would throw or emit a corrupted response. Bail out and let the request tear down.
    if (context.Response.HasStarted)
    {
      _logger.LogWarning(
        "Response already started; cannot write error payload. TraceId: {TraceId}", context.TraceIdentifier);
      return;
    }

    context.Response.ContentType = "application/json";

    var isDevelopment = _environment.IsDevelopment();
    var traceId = context.TraceIdentifier;

    object response;

    switch (exception)
    {
      // Authorization/ownership denials from services (e.g. "User does not have permission
      // to edit this budget") are an authenticated-but-forbidden caller — a 403, NOT a 401.
      // Mapping these to 401 makes the client treat a permission error like an expired session:
      // it silently refreshes (the token was valid), retries, 401s again, and force-logs-out a
      // user who merely lacked permission for one action. 401 is reserved for missing/invalid
      // tokens, which the JWT middleware handles directly (never as this exception).
      case UnauthorizedAccessException:
        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        response = new { message = "You do not have permission to perform this action.", traceId };
        break;

      case ArgumentException:
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        response = new { message = exception.Message, traceId };
        break;

      case KeyNotFoundException:
        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        response = new { message = "Resource not found", traceId };
        break;

      // BuoyAI Cloud says the AI allowance (or the customer's pay-as-you-go limit) is used up: a plain 402 the app can act on.
      // Precedes AiUnavailableException, which it derives from.
      case AiAllowanceException allowance:
        context.Response.StatusCode = (int)HttpStatusCode.PaymentRequired;
        response = new { message = allowance.Message, code = allowance.Code, canEnablePayg = allowance.CanEnablePayg, traceId };
        break;

      // No Gemini key configured: the feature is off, not broken. Say so (503, safe message).
      case AiUnavailableException:
        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
        response = new { message = exception.Message, traceId };
        break;

      // Intentional business-rule violations (e.g. "Insufficient funds") are safe to echo as 400.
      // This MUST precede the InvalidOperationException case since DomainValidationException derives
      // from it.
      case DomainValidationException:
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        response = new { message = exception.Message, traceId };
        break;

      // Everything else — including a bare InvalidOperationException, which is almost always a
      // framework/EF fault (bad LINQ, "sequence contains no elements", DbUpdateConcurrencyException,
      // etc.) rather than a client-correctable domain rule. These surface as a generic 500 (counted
      // toward 5xx) and never leak their raw message in production, instead of the old behaviour that
      // blanket-mapped every InvalidOperationException to 400 with the exception text.
      default:
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        
        // In production, return a generic message and trace ID for debugging
        // In development, include exception details for easier debugging
        if (isDevelopment)
        {
          response = new 
          { 
            message = "An error occurred while processing your request",
            details = exception.Message,
            stackTrace = exception.StackTrace,
            innerException = exception.InnerException?.Message,
            traceId
          };
        }
        else
        {
          // Production: Generic message with trace ID for log correlation
          response = new 
          { 
            message = "An internal error occurred. Please try again later.",
            traceId
          };
          
          // Log additional context for production debugging
          _logger.LogError(
            "Internal server error. TraceId: {TraceId}, Exception: {ExceptionType}, Message: {Message}", 
            traceId, 
            exception.GetType().Name, 
            exception.Message);
        }
        break;
    }

    var jsonResponse = JsonSerializer.Serialize(response, new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });

    await context.Response.WriteAsync(jsonResponse);
  }
}
