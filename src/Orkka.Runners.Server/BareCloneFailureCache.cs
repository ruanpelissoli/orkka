namespace Orkka.Runners.Server;

using System.Collections.Concurrent;
using Orkka.Modules.Agents.Contracts;

/// <summary>
/// Short-lived memory of bare clones that just failed, so one broken repo costs one attempt
/// per window instead of one per chat turn.
///
/// The case this was built for: a user whose runner has no git credential helper for the
/// remote. Every clone fails identically and instantly, and seven chat turns over seven
/// minutes fired twenty-one of them. Nothing about the outcome could have changed between
/// them — the clone is the same command against the same remote from the same machine.
///
/// Deliberately NOT a permanent cache. Fixing the credentials is a thing the user does on
/// their own machine with no way to tell us about it, so the window has to be short enough
/// that "sign in, send another message" works. It is also not a positive cache: a
/// successful <c>EnsureBareClone</c> also fetches, and skipping that would hand the agent
/// yesterday's code.
///
/// Process-wide by default (<see cref="Shared"/>) because the hosted relay constructs a
/// fresh <see cref="RunnerChatInvoker"/> per request — a per-instance cache there would
/// never see a second turn. Tests pass their own instance.
/// </summary>
public sealed class BareCloneFailureCache
{
    /// <summary>How long a failure the user must fix (credentials, a repo that isn't there)
    /// suppresses retries. Long enough to collapse a burst of turns — the production case
    /// was 21 identical clones in 7 minutes — and short enough to keep the promise the
    /// user-facing notice makes, which is that signing in takes effect within a few
    /// minutes. Change one and the other has to change with it.</summary>
    public static readonly TimeSpan DefaultPermanentWindow = TimeSpan.FromMinutes(3);

    /// <summary>How long anything else suppresses retries — a transport blip, an offline
    /// runner, an error we could not classify. Kept brief on purpose: these clear on their
    /// own, and caching a runner that has since reconnected strands the whole codebase.</summary>
    public static readonly TimeSpan DefaultTransientWindow = TimeSpan.FromSeconds(30);

    /// <summary>Entries are dropped as they expire, but a cache nobody reads again would
    /// still grow — sweep when it gets large rather than on a timer.</summary>
    private const int SweepThreshold = 512;

    private readonly ConcurrentDictionary<(Ulid Org, Ulid Repo), Entry> _entries = new();
    private readonly TimeSpan _permanentWindow;
    private readonly TimeSpan _transientWindow;
    private readonly TimeProvider _time;

    public BareCloneFailureCache(
        TimeSpan? permanentWindow = null,
        TimeSpan? transientWindow = null,
        TimeProvider? timeProvider = null)
    {
        _permanentWindow = permanentWindow ?? DefaultPermanentWindow;
        _transientWindow = transientWindow ?? DefaultTransientWindow;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The instance real hosts use. See the type remarks for why it is static.</summary>
    public static BareCloneFailureCache Shared { get; } = new();

    /// <summary>The still-valid failure for this repo, or null when a fresh attempt is due.
    /// An expired entry is removed as it is read, so the next caller attempts for real.</summary>
    public CachedCloneFailure? TryGet(Ulid orgId, Ulid repoId)
    {
        var key = (orgId, repoId);
        if (!_entries.TryGetValue(key, out var entry))
            return null;

        if (_time.GetUtcNow() < entry.ExpiresAt)
            return new CachedCloneFailure(entry.Reason, entry.Error);

        _entries.TryRemove(key, out _);
        return null;
    }

    /// <summary>Remembers a failed clone. The window depends on whether the reason can
    /// clear by itself — see the two window fields.</summary>
    public void Record(Ulid orgId, Ulid repoId, RepoAccessFailureReason reason, string? error)
    {
        var window = RepoAccessDiagnostics.IsPermanent(reason) ? _permanentWindow : _transientWindow;
        _entries[(orgId, repoId)] = new Entry(reason, error, _time.GetUtcNow() + window);

        if (_entries.Count >= SweepThreshold)
            Sweep();
    }

    /// <summary>Forgets this repo's failure — called when a clone succeeds, so a repo that
    /// starts working is never held back by a stale entry.</summary>
    public void Clear(Ulid orgId, Ulid repoId) => _entries.TryRemove((orgId, repoId), out _);

    private void Sweep()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (now >= entry.ExpiresAt)
                _entries.TryRemove(key, out _);
        }
    }

    private readonly record struct Entry(
        RepoAccessFailureReason Reason, string? Error, DateTimeOffset ExpiresAt);
}

/// <summary>A remembered clone failure, replayed in place of an attempt.</summary>
public sealed record CachedCloneFailure(RepoAccessFailureReason Reason, string? Error);
