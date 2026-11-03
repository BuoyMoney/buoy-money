using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using System.Security.Claims;

namespace BuoyAI.API.Attributes;

/// <summary>
/// Authorization attribute that enforces MFA requirements for protected operations.
/// Use this attribute on controller actions that require step-up authentication.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class RequiresMfaAttribute : Attribute, IAsyncActionFilter
{
    public ProtectedOperation Operation { get; }

    public RequiresMfaAttribute(ProtectedOperation operation)
    {
        Operation = operation;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var stepUpAuthService = context.HttpContext.RequestServices
            .GetService<IStepUpAuthService>();
        var logger = context.HttpContext.RequestServices
            .GetService<ILogger<RequiresMfaAttribute>>();

        if (stepUpAuthService == null)
        {
            context.Result = new StatusCodeResult(500);
            return;
        }

        // Get user ID from claims - try standard ClaimTypes.NameIdentifier first, then 'nameid' fallback
        var userIdClaim = context.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim))
        {
            userIdClaim = context.HttpContext.User.FindFirst("nameid")?.Value;
        }
        
        if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out var userId))
        {
            logger?.LogWarning("RequiresMfa: No valid user ID claim found. Claims: {Claims}", 
                string.Join(", ", context.HttpContext.User.Claims.Select(c => $"{c.Type}={c.Value}")));
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Unauthorized",
                message = "User not authenticated"
            });
            return;
        }
        
        logger?.LogInformation("RequiresMfa: User {UserId} accessing {Operation}", userId, Operation);

        // A verified email is required before any protected operation. This confirms the
        // address is real/reachable and that our email-delivered code (the final MFA
        // fallback) actually works. OAuth sign-ups are pre-verified.
        var userRepository = context.HttpContext.RequestServices.GetService<IUserRepository>();
        if (userRepository != null)
        {
            var user = await userRepository.GetByIdAsync(userId);
            if (user != null && !user.IsEmailVerified)
            {
                logger?.LogInformation("RequiresMfa: User {UserId} blocked — email not verified", userId);
                context.Result = new ObjectResult(new
                {
                    error = "EmailVerificationRequired",
                    message = "Please verify your email address before performing this action.",
                    operation = Operation.ToString()
                })
                {
                    StatusCode = 403
                };
                return;
            }
        }

        // Check if operation token is provided in Authorization header
        var operationToken = context.HttpContext.Request.Headers["X-Operation-Token"].FirstOrDefault();

        if (!string.IsNullOrEmpty(operationToken))
        {
            // Validate the operation token
            var isValidToken = await stepUpAuthService.ValidateOperationTokenAsync(
                operationToken, 
                Operation, 
                userId);

            if (isValidToken)
            {
                await next(); // Token is valid, proceed with action
                return;
            }
        }

        // Check if MFA is required for this operation
        var requiresMfa = await stepUpAuthService.RequiresMfaForOperationAsync(userId, Operation);

        if (requiresMfa)
        {
            context.Result = new ObjectResult(new
            {
                error = "MfaRequired",
                message = "This operation requires multi-factor authentication",
                operation = Operation.ToString(),
                requirementLevel = stepUpAuthService.GetRequirementLevel(Operation).ToString()
            })
            {
                StatusCode = 403
            };
            return;
        }

        // No MFA required or valid token provided, proceed with action
        await next();
    }
}
