using System.Security.Claims;
using BuoyAI.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace BuoyAI.API.Authentication;

/// <summary>
/// Rejects an otherwise-valid first-party JWT whose user row no longer exists (or is soft-deleted).
///
/// JwtBearer only checks signature/issuer/audience/expiry. Without this hook a token issued to a
/// user that was since deleted (account deletion, or the staging DemoAccountSeeder wiping and
/// re-creating demo accounts mid-session) still authenticates, and every controller then queries
/// by a user id that resolves to nothing: empty collections and 404s instead of a 401. Failing the
/// ticket here yields a 401, which is what the Flutter ApiClient interceptor's refresh-then-logout
/// flow expects.
///
/// Cost: one indexed primary-key lookup per request, memoised per user id for
/// <see cref="CacheTtl"/>. Only positive results are cached, so a re-created id (same PK) is
/// picked up on the next request and a freshly deleted user is cut off within one TTL.
/// </summary>
public static class JwtUserExistenceValidator
{
  public const string FailureMessage = "User no longer exists";

  /// <summary>How long a confirmed-existing user id is trusted before re-checking the DB.</summary>
  public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

  private const string CacheKeyPrefix = "jwt-user-exists:";

  public static async Task ValidateAsync(TokenValidatedContext context)
  {
    var identity = context.Principal?.Identity as ClaimsIdentity;
    var rawId = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? identity?.FindFirst("nameid")?.Value;

    if (string.IsNullOrEmpty(rawId) || !int.TryParse(rawId, out var userId))
    {
      // No usable subject claim: BaseController.GetCurrentUserId would throw anyway; be explicit.
      context.Fail("Token has no valid user id claim");
      return;
    }

    var services = context.HttpContext.RequestServices;
    var cache = services.GetService<IMemoryCache>();
    var cacheKey = CacheKeyPrefix + userId;

    if (cache != null && cache.TryGetValue(cacheKey, out _))
    {
      return;
    }

    var db = services.GetRequiredService<BuoyAIDbContext>();
    var exists = await db.Users
      .AsNoTracking()
      .AnyAsync(u => u.Id == userId && !u.IsDeleted, context.HttpContext.RequestAborted);

    if (!exists)
    {
      context.Fail(FailureMessage);
      return;
    }

    cache?.Set(cacheKey, true, CacheTtl);
  }
}
