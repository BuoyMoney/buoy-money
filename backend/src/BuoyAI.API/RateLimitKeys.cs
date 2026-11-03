namespace BuoyAI.API;

/// <summary>Partition keys for the rate limiters.</summary>
public static class RateLimitKeys
{
    /// <summary>
    /// Real client IP for rate-limit partitioning. Hosted sits behind an nginx chain, where
    /// Connection.RemoteIpAddress is the proxy (127.0.0.1) and would collapse every client into one
    /// shared bucket, so it prefers the original client from X-Forwarded-For / X-Real-IP. Those
    /// headers are client-supplied, which only lets an attacker throttle themselves there.
    ///
    /// Self-host publishes Kestrel directly, where a spoofed header would mint a fresh bucket per
    /// request and defeat the login limit. So <paramref name="remoteAddressOnly"/> keys on
    /// RemoteIpAddress, which UseForwardedHeaders has already rewritten for trusted proxies only.
    /// </summary>
    public static string ClientIp(HttpContext ctx, bool remoteAddressOnly)
    {
        if (remoteAddressOnly) return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var forwardedFor = ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            var first = forwardedFor.Split(',')[0].Trim();
            if (first.Length > 0) return first;
        }
        var realIp = ctx.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp)) return realIp.Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
