namespace Orkka.SharedKernel.Utilities;

using System.Collections.Concurrent;

/// <summary>
/// An in-process async lock per key (an org, a user). Orkka runs as ONE process on one machine,
/// so this is the whole of "serialize these writers" — what a database advisory lock did when
/// several replicas shared a server. Entries are reference-counted and dropped when the last
/// holder leaves, so the map never grows with every key ever seen.
/// </summary>
public sealed class KeyedLock<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _entries;

    public KeyedLock(IEqualityComparer<TKey>? comparer = null) =>
        _entries = new ConcurrentDictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);

    /// <summary>Waits for <paramref name="key"/>'s lock; dispose the result to release it.</summary>
    public async Task<IAsyncDisposable> AcquireAsync(TKey key, CancellationToken ct = default)
    {
        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(key, static _ => new Entry());
            lock (entry)
            {
                // A releaser may have just retired this entry; take a fresh one if so.
                if (entry.Retired) continue;
                entry.RefCount++;
            }
            break;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    /// <summary>Keys with a holder or a waiter right now (for tests).</summary>
    public int ActiveKeys => _entries.Count;

    private void Leave(TKey key, Entry entry)
    {
        lock (entry)
        {
            if (--entry.RefCount > 0) return;
            entry.Retired = true;
        }
        _entries.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
        public bool Retired;
    }

    private sealed class Releaser(KeyedLock<TKey> owner, TKey key, Entry entry) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                entry.Semaphore.Release();
                owner.Leave(key, entry);
            }
            return ValueTask.CompletedTask;
        }
    }
}
