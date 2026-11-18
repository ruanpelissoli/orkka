namespace Orkka.Local.Auth;

using System.Collections.Concurrent;
using System.Buffers.Text;
using System.Security.Cryptography;

/// <summary>
/// One-time launch codes: <c>./orkka start|open</c> mints one with the install secret and
/// opens <c>/launch?code=…</c> in the browser, which trades it for the session cookie. A code
/// works once, for <see cref="Lifetime"/>, and only its hash is held — in memory only, so a
/// restart voids every outstanding code.
/// </summary>
public sealed class LaunchCodeStore(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _codes = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string Issue()
    {
        var now = _clock.GetUtcNow();
        foreach (var (hash, expiresAt) in _codes)
        {
            if (expiresAt <= now)
                _codes.TryRemove(hash, out _);
        }

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _codes[LocalSessionStore.Hash(code)] = now + Lifetime;
        return code;
    }

    /// <summary>True exactly once per issued, unexpired code.</summary>
    public bool TryConsume(string? code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 256)
            return false;
        // Removed whether or not it expired: a late code is spent either way.
        return _codes.TryRemove(LocalSessionStore.Hash(code), out var expiresAt)
            && expiresAt > _clock.GetUtcNow();
    }
}
