namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// Says "the calls made inside this block are this user's". The provider seam is text in, text out and has no user on it, but
/// every call a person causes (insights, receipts, recap wording, memory, subscription wording) has to land in their usage
/// rows. A caller wraps its model call in <c>using (AiUsageScope.ForUser(id))</c>; <see cref="MeteredAiProvider"/> reads it.
/// Work with no person behind it (a broadcast, a benchmark) sets nothing and writes no per-user row.
/// </summary>
public static class AiUsageScope
{
    private static readonly AsyncLocal<int?> Current = new();

    public static int? CurrentUserId => Current.Value;

    public static IDisposable ForUser(int userId)
    {
        var previous = Current.Value;
        Current.Value = userId;
        return new Restore(previous);
    }

    private sealed class Restore(int? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
