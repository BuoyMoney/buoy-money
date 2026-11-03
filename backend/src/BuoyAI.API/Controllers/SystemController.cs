using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using BuoyAI.API.Extensions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services;
using Sentry;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Provides system status and diagnostics endpoints.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("api")]
public class SystemController : BaseController
{
    private readonly ICircuitBreakerService _circuitBreakerService;
    private readonly ILogger<SystemController> _logger;
    private readonly EditionInfo _edition;
    private readonly IConfiguration _configuration;

    public SystemController(
        ICircuitBreakerService circuitBreakerService,
        ILogger<SystemController> logger,
        EditionInfo edition,
        IConfiguration configuration)
    {
        _circuitBreakerService = circuitBreakerService;
        _logger = logger;
        _edition = edition;
        _configuration = configuration;
    }

    /// <summary>
    /// What this server is and which apps it will talk to. The app asks this before it signs anyone
    /// in: a self-hosted server can lag behind the app (the app then shows "your server needs an
    /// update"), and an old app can lag behind the server (the server names the oldest app version
    /// it still supports in <c>minClientVersion</c>). Anonymous and read-only.
    /// </summary>
    [HttpGet("version")]
    [AllowAnonymous]
    public IActionResult GetVersion()
    {
        var selfHost = _edition.IsSelfHost;
        // Single sign-on (OpenID Connect) is a self-host feature only.
        var oidc = selfHost ? OidcOptions.From(_configuration) : null;
        var oidcOn = oidc?.IsConfigured == true;
        return Ok(new
        {
            version = AppVersion.Current,
            minClientVersion = _configuration["System:MinClientVersion"] is { Length: > 0 } min ? min : "1.0.0",
            edition = _edition.Name,
            // Where a phone pairs for relayed push (self-host only; see docs/self_host/push_relay.md).
            // The operator's own Google web client id (a public identifier). The app must start
            // Google with this id, or the token's audience will not match the server's.
            googleClientId = selfHost && _configuration["GoogleAuth:ClientId"] is { Length: > 0 } gid ? gid : null,
            pushRelayUrl = selfHost && _configuration["PushRelay:Url"] is { Length: > 0 } relay ? relay.TrimEnd('/') : null,
            // The label for "Sign in with ..." when single sign-on is on.
            oidcDisplayName = oidcOn ? oidc!.DisplayName : null,
            // What this server can actually do, so the app hides what would only fail. Hosted: all true.
            features = new
            {
                googleSignIn = !selfHost || !string.IsNullOrWhiteSpace(_configuration["GoogleAuth:ClientId"]),
                passkeys = !selfHost || PasskeysUsable(_configuration["WebAuthn:Origin"]),
                email = !selfHost || HttpContext?.RequestServices?.GetService<IEmailTransport>()?.IsConfigured == true,
                oidc = oidcOn,
                // False only when the operator turned passwords off in favour of single sign-on.
                passwordLogin = !(oidc?.PasswordLoginDisabled ?? false)
            }
        });
    }

    /// <summary>Browsers allow passkeys only on https, or on localhost for development.</summary>
    private static bool PasskeysUsable(string? origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);

    /// <summary>
    /// Gets the status of all circuit breakers.
    /// </summary>
    /// <returns>Circuit breaker statistics for all external services</returns>
    [HttpGet("circuits")]
    [AllowAnonymous]  // Allow monitoring tools to access
    public IActionResult GetCircuitBreakerStatus()
    {
        var circuits = _circuitBreakerService.GetAllCircuitStatistics()
            .Select(c => new
            {
                c.Name,
                State = c.State.ToString(),
                c.FailureCount,
                c.SuccessCount,
                c.LastStateChange,
                IsHealthy = c.State == CircuitStates.Closed
            })
            .ToList();
        
        // Add standard circuit names even if not yet used
        var standardCircuits = new[] { "Plaid", "Stripe", "Gemini" };
        foreach (var name in standardCircuits)
        {
            if (!circuits.Any(c => c.Name == name))
            {
                circuits.Add(new
                {
                    Name = name,
                    State = CircuitStates.Closed.ToString(),
                    FailureCount = 0,
                    SuccessCount = 0,
                    LastStateChange = (DateTime?)null,
                    IsHealthy = true
                });
            }
        }

        var allHealthy = circuits.All(c => c.IsHealthy);
        
        return Ok(new
        {
            Status = allHealthy ? "Healthy" : "Degraded",
            Timestamp = DateTime.UtcNow,
            Circuits = circuits.OrderBy(c => c.Name)
        });
    }

    /// <summary>
    /// Resets a specific circuit breaker (admin only).
    /// </summary>
    [HttpPost("circuits/{circuitName}/reset")]
    [Authorize(Roles = "Admin")]
    public IActionResult ResetCircuitBreaker(string circuitName)
    {
        _circuitBreakerService.ResetCircuit(circuitName);
        
        _logger.LogInformation("Circuit breaker {CircuitName} reset by admin", circuitName);
        
        return Ok(new { Message = $"Circuit breaker '{circuitName}' has been reset" });
    }

    /// <summary>
    /// Tests Sentry error reporting by throwing a test exception.
    /// Only available in non-production environments.
    /// </summary>
    [HttpPost("sentry-test")]
    [Authorize(Roles = "Admin")]
    public IActionResult TestSentryError([FromServices] IWebHostEnvironment env)
    {
        if (env.IsProduction())
        {
            return BadRequest(new { Message = "Sentry test is not available in production" });
        }

        var testException = new InvalidOperationException("Test exception for Sentry verification");
        
        // Capture directly to Sentry
        var eventId = SentrySdk.CaptureException(testException, scope =>
        {
            scope.SetTag("test", "true");
            scope.SetTag("triggered_by", "admin-test-endpoint");
        });
        
        _logger.LogWarning(testException, "Sentry test exception triggered. EventId: {EventId}", eventId);
        
        return Ok(new 
        { 
            Message = "Test exception sent to Sentry",
            EventId = eventId.ToString(),
            IsEnabled = SentrySdk.IsEnabled
        });
    }
}
