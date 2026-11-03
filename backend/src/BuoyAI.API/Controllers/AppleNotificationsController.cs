using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Apple "Sign in with Apple" server-to-server notification webhook. Apple POSTs a
/// signed JWT here when a user changes mail-forwarding, deletes their app account, or
/// permanently deletes their Apple Account. Anonymous like the Stripe webhook; the
/// payload's signature is verified against Apple's JWKS before any action is taken.
/// Register this endpoint's URL in the Apple Developer portal (App ID → Sign in with
/// Apple → Server-to-Server Notification Endpoint).
/// </summary>
[ApiController]
[Route("api/auth/apple")]
public class AppleNotificationsController : ControllerBase
{
    private readonly IAppleTokenValidator _appleTokenValidator;
    private readonly IUserRepository _userRepository;
    private readonly IAuthService _authService;
    private readonly ILogger<AppleNotificationsController> _logger;

    public AppleNotificationsController(
        IAppleTokenValidator appleTokenValidator,
        IUserRepository userRepository,
        IAuthService authService,
        ILogger<AppleNotificationsController> logger)
    {
        _appleTokenValidator = appleTokenValidator;
        _userRepository = userRepository;
        _authService = authService;
        _logger = logger;
    }

    [HttpPost("notifications")]
    public async Task<IActionResult> ReceiveNotification([FromBody] AppleNotificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Payload))
            return Ok();

        var notification = await _appleTokenValidator.ValidateServerNotificationAsync(request.Payload);
        if (notification == null)
        {
            _logger.LogWarning("Received an unverifiable Apple server notification.");
            // Return 200 regardless: Apple retries can't fix a malformed/forged payload,
            // and we don't want to signal validity to a caller probing the endpoint.
            return Ok();
        }

        _logger.LogInformation("Apple server notification: type={Type} sub={Sub}",
            notification.Type, notification.Subject);

        switch (notification.Type)
        {
            case "account-delete":
            case "consent-revoked":
                // The user deleted their Apple Account, or revoked Sign in with Apple for
                // our app. Per Apple's requirement, delete the corresponding account.
                var user = await _userRepository.GetByAppleIdAsync(notification.Subject);
                if (user != null && !user.IsDeleted)
                {
                    await _authService.DeleteAccountAsync(user.Id);
                    _logger.LogInformation(
                        "Deleted account for user {UserId} on Apple '{Type}' notification.",
                        user.Id, notification.Type);
                }
                break;

            case "email-disabled":
            case "email-enabled":
                // Private-relay forwarding toggled — logged for observability; nothing to
                // enforce server-side today.
                break;
        }

        return Ok();
    }
}

/// <summary>Body of an Apple server-to-server notification: a single signed JWT.</summary>
public class AppleNotificationRequest
{
    public string? Payload { get; set; }
}
