using System.Security.Claims;

namespace BuoyAI.API.Extensions;

/// <summary>
/// Extension methods for ClaimsPrincipal
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Gets the user ID from the claims
    /// </summary>
    /// <param name="user">The ClaimsPrincipal</param>
    /// <returns>The user ID</returns>
    public static int GetUserId(this ClaimsPrincipal user)
    {
        var userIdClaim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (int.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }
        
        throw new InvalidOperationException("User ID not found in claims");
    }
    
    /// <summary>
    /// Gets the user email from the claims
    /// </summary>
    /// <param name="user">The ClaimsPrincipal</param>
    /// <returns>The user email</returns>
    public static string GetUserEmail(this ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.Email)?.Value 
            ?? throw new InvalidOperationException("User email not found in claims");
    }
    
    /// <summary>
    /// Gets the username from the claims
    /// </summary>
    /// <param name="user">The ClaimsPrincipal</param>
    /// <returns>The username</returns>
    public static string GetUsername(this ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.Name)?.Value 
            ?? throw new InvalidOperationException("Username not found in claims");
    }
}
