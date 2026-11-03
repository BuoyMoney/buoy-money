using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>What a link state stands for: the admin who started it and the name they gave this server.</summary>
public sealed record CloudLinkState(int UserId, string? Name);

/// <summary>
/// One-time states for "Link with BuoyAI Cloud". The admin starts a link here, the browser goes to buoy-ai.com with
/// the state, and comes back to the anonymous callback with it; the state is what proves the callback belongs to a link
/// this server's admin started. 32 random bytes, only the SHA-256 is kept, good for 15 minutes and spent on first use.
/// Held in process memory (a self-hosted server is a single process), so a restart drops pending links.
/// </summary>
public class CloudLinkStateStore
{
  public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
  private const int MaxStateLength = 200;
  private readonly IMemoryCache _cache;
  private readonly TimeProvider _time;
  private readonly object _gate = new();

  public CloudLinkStateStore(IMemoryCache cache, TimeProvider time)
  {
    _cache = cache;
    _time = time;
  }

  private sealed record Entry(int UserId, string? Name, DateTimeOffset ExpiresAt);

  /// <summary>Makes a new state for <paramref name="userId"/>. The returned value is the only copy of the secret.</summary>
  public string Create(int userId, string? name)
  {
    var state = Base64Url(RandomNumberGenerator.GetBytes(32));
    var expires = _time.GetUtcNow() + Lifetime;
    _cache.Set(Key(state), new Entry(userId, name, expires), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime });
    return state;
  }

  /// <summary>Spends a state. Null when it is unknown, already used or expired. A state works once, even when it fails.</summary>
  public CloudLinkState? Consume(string? state)
  {
    if (string.IsNullOrEmpty(state) || state.Length > MaxStateLength) return null;
    var key = Key(state);
    Entry? entry;
    lock (_gate)
    {
      if (!_cache.TryGetValue(key, out entry) || entry == null) return null;
      _cache.Remove(key);
    }
    return entry.ExpiresAt <= _time.GetUtcNow() ? null : new CloudLinkState(entry.UserId, entry.Name);
  }

  private static string Key(string state) =>
    "cloud-link-state:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

  private static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
