using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Utilities;

/// <summary>
/// Decides whether a connection is due for a sync attempt, given its provider's minimum interval.
/// The background loop wakes every ten minutes for every provider; this is what keeps a provider
/// with a request budget (SimpleFIN asks for 24 calls a day) from being hit on every wake.
/// </summary>
public static class SyncThrottle
{
    /// <summary>
    /// True when at least <paramref name="minimumInterval"/> plus this connection's spread has
    /// passed since the last attempt (failed attempts count, so a broken connection is not retried
    /// every cycle). A connection never attempted is always due. An interval of zero means always.
    /// </summary>
    public static bool IsDue(PlaidConnection connection, TimeSpan minimumInterval, TimeSpan jitter, DateTime nowUtc)
    {
        if (minimumInterval <= TimeSpan.Zero)
        {
            return true;
        }

        var lastAttempt = connection.LastSyncAttemptAt ?? connection.LastTransactionSyncAt;
        if (lastAttempt == null)
        {
            return true;
        }

        return nowUtc >= lastAttempt.Value + minimumInterval + JitterFor(connection.Id, jitter);
    }

    /// <summary>
    /// A fixed share of <paramref name="maxJitter"/> for this connection, between none and all of it.
    /// Stable per connection (so the schedule does not wander) and different between connections
    /// (so they do not all fire in the same ten-minute cycle).
    /// </summary>
    public static TimeSpan JitterFor(int connectionId, TimeSpan maxJitter)
    {
        if (maxJitter <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var spread = unchecked((uint)connectionId * 2654435761u) % 1000u;
        return TimeSpan.FromTicks((long)(maxJitter.Ticks * (spread / 1000.0)));
    }
}
