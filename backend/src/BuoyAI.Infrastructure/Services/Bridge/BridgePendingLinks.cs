using System.Collections.Concurrent;

namespace BuoyAI.Infrastructure.Services.Bridge;

/// <summary>One link page this server started for a user. <see cref="ConnectionId"/> is set for a reconnect.</summary>
public record PendingBridgeLink(string SessionId, int UserId, int? ConnectionId, DateTime ExpiresAt);

/// <summary>
/// Which user started which Cloud link page. The Cloud keys banks by server, not by person, so this server must remember
/// who asked: only the user who started a page may adopt the bank that page made. Held in memory for the life of the page
/// (about half an hour); a restart in that window means the person starts the page again.
/// </summary>
public class BridgePendingLinks
{
  private readonly ConcurrentDictionary<string, PendingBridgeLink> _links = new(StringComparer.Ordinal);
  private readonly TimeProvider _time;

  public BridgePendingLinks(TimeProvider time) => _time = time;

  public void Add(PendingBridgeLink link)
  {
    Prune();
    _links[link.SessionId] = link;
  }

  /// <summary>The page this user started: the named one, or when the caller names none, their most recent.</summary>
  public PendingBridgeLink? Find(int userId, string? sessionId)
  {
    Prune();
    if (!string.IsNullOrEmpty(sessionId))
      return _links.TryGetValue(sessionId, out var named) && named.UserId == userId ? named : null;
    return _links.Values.Where(l => l.UserId == userId).OrderByDescending(l => l.ExpiresAt).FirstOrDefault();
  }

  public void Remove(string sessionId) => _links.TryRemove(sessionId, out _);

  private void Prune()
  {
    var now = _time.GetUtcNow().UtcDateTime;
    foreach (var (id, link) in _links)
      if (link.ExpiresAt.AddMinutes(30) < now) _links.TryRemove(id, out _);
  }
}
