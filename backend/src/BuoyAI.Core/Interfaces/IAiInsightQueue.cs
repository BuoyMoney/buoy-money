namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Fire-and-forget queue for AI-insight generation. Read paths (dashboard insights,
/// savings overview) enqueue a user+context on a cache miss instead of calling Gemini
/// synchronously in the request thread — a slow LLM call was pushing those endpoints'
/// P95 over the 2s latency alert. A background service drains the queue and runs the
/// generation off-thread. Generation is 24h-cache-guarded and the reader is
/// single-threaded, so duplicate enqueues cost at most one LLM call per user+context.
/// </summary>
public interface IAiInsightQueue
{
  /// <summary>
  /// Queue an insight-generation job. No-op for <paramref name="userId"/> &lt;= 0 or a
  /// blank <paramref name="context"/>.
  /// </summary>
  void Enqueue(int userId, string context, int maxInsights);
}
