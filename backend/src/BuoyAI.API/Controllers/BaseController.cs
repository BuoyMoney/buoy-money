using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

namespace BuoyAI.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("api")]
public abstract class BaseController : ControllerBase
{
  protected int GetCurrentUserId()
  {
    // Try to find standard ClaimTypes.NameIdentifier first
    var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    
    // If not found, try the JWT 'nameid' claim directly
    if (string.IsNullOrEmpty(userIdClaim))
    {
      userIdClaim = User.FindFirst("nameid")?.Value;
    }
    
    if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out var userId))
    {
      throw new UnauthorizedAccessException("Invalid user context");
    }
    return userId;
  }

  protected string GetCurrentUsername()
  {
    // Try to find standard ClaimTypes.Name first
    var username = User.FindFirst(ClaimTypes.Name)?.Value;
    
    // If not found, try the JWT 'unique_name' claim directly
    if (string.IsNullOrEmpty(username))
    {
      username = User.FindFirst("unique_name")?.Value;
    }
    
    return username ?? throw new UnauthorizedAccessException("Invalid user context");
  }

  protected IActionResult HandleServiceResponse<T>(T response, string? errorMessage = null)
  {
    if (response == null)
    {
      return NotFound(new { message = errorMessage ?? "Resource not found" });
    }

    return Ok(response);
  }
}
