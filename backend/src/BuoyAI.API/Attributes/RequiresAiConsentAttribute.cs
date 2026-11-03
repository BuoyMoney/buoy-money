using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

namespace BuoyAI.API.Attributes;

/// <summary>
/// Gate in front of every endpoint that sends the user's personal data to a third-party
/// AI (Google Gemini). App Store Review Guidelines 5.1.1(i)/5.1.2(i) require the app to say
/// what is sent and to whom and get explicit permission first. Anyone without a current
/// grant (<see cref="AiConsent.IsCurrent"/>) is refused with the typed error code
/// <c>AiConsentRequired</c>, on every platform, and the client opens the consent sheet.
///
/// There used to be an implied grant outside iOS, stamped on first AI use. Because consent
/// lives on the user, not the device, a grant implied on Android also counted on iOS: that
/// is how App Review's shared demo account reached AI Chat on an iPad without ever seeing
/// the sheet (rejection of 2026-09-26). One explicit rule everywhere removes that path.
///
/// Apply per action, only to endpoints that actually call a model: endpoints that merely
/// read stored AI output stay open so a user who declined can still see and delete it.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public class RequiresAiConsentAttribute : Attribute, IAsyncActionFilter
{
  public const string ErrorCode = "AiConsentRequired";
  public const string ClientTypeHeader = "X-Client-Type";
  public const string IosClientType = "mobile-ios";

  public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
  {
    var users = context.HttpContext.RequestServices.GetService<IUserRepository>();
    var logger = context.HttpContext.RequestServices.GetService<ILogger<RequiresAiConsentAttribute>>();
    if (users == null)
    {
      context.Result = new StatusCodeResult(500);
      return;
    }

    var rawId = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (!int.TryParse(rawId, out var userId))
    {
      context.Result = new UnauthorizedResult();
      return;
    }

    var user = await users.GetByIdAsync(userId);
    if (user == null)
    {
      context.Result = new UnauthorizedResult();
      return;
    }

    if (!AiConsent.IsCurrent(user))
    {
      var reason = user.AiConsentWithdrawnAt != null && user.AiConsentGrantedAt == null
        ? "withdrawn"
        : user.AiConsentGrantedAt != null ? "consent predates the current disclosure" : "undecided";
      Refuse(context, logger, userId, reason);
      return;
    }

    await next();
  }

  private static void Refuse(ActionExecutingContext context, ILogger? logger, int userId, string reason)
  {
    logger?.LogInformation("RequiresAiConsent: user {UserId} blocked ({Reason})", userId, reason);
    context.Result = new ObjectResult(new
    {
      error = ErrorCode,
      message = "Turn on AI features to continue. Buoy Money sends your transaction data to Google Gemini only after you agree.",
    })
    { StatusCode = 403 };
  }
}
