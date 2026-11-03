namespace BuoyAI.API.Services;

/// <summary>
/// Caps concurrent full data exports: at most <c>maxConcurrent</c> across the whole server (the
/// prod box has 4 GB) and at most one per user, so one account cannot hold every slot with slow
/// reads. A request that cannot get a slot is turned away with 429 instead of queueing.
/// </summary>
public sealed class ExportConcurrencyGate
{
    public const int DefaultMaxConcurrent = 2;

    private readonly SemaphoreSlim _slots;
    private readonly HashSet<int> _activeUsers = new();
    private readonly object _lock = new();

    public ExportConcurrencyGate(int maxConcurrent = DefaultMaxConcurrent)
    {
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    /// <summary>
    /// Takes a slot for <paramref name="userId"/> if the server has one free and that user has no
    /// export running. Dispose the returned lease to give the slot back.
    /// </summary>
    public IDisposable? TryEnter(int userId)
    {
        lock (_lock)
        {
            if (_activeUsers.Contains(userId) || !_slots.Wait(0))
            {
                return null;
            }
            _activeUsers.Add(userId);
        }
        return new Lease(this, userId);
    }

    private void Release(int userId)
    {
        lock (_lock)
        {
            _activeUsers.Remove(userId);
            _slots.Release();
        }
    }

    private sealed class Lease : IDisposable
    {
        private ExportConcurrencyGate? _gate;
        private readonly int _userId;

        public Lease(ExportConcurrencyGate gate, int userId)
        {
            _gate = gate;
            _userId = userId;
        }

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release(_userId);
    }
}
