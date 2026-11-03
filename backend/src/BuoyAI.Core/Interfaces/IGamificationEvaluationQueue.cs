namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Fire-and-forget queue for gamification re-evaluation. Write paths (budget created,
/// savings contributed, transaction categorized, …) enqueue the acting user's id; a
/// background service drains the queue and runs achievement/streak evaluation off the
/// request thread. Evaluation is idempotent, so duplicate enqueues are harmless.
/// </summary>
public interface IGamificationEvaluationQueue
{
  /// <summary>Queue a user for (non-blocking) gamification re-evaluation. No-op for ids &lt;= 0.</summary>
  void Enqueue(int userId);
}
