namespace Orkka.Local.Auth;

using System.Collections.Concurrent;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Browser sessions: the <c>orkka_s</c> cookie value is a random token; only its SHA-256 hash is
/// kept — in memory, and persisted to <c>{DataDir}/sessions.json</c> so a restart does not sign
/// the browser out. Sliding 30-day lifetime: a session used after <see cref="RefreshAfter"/>
/// is pushed out again (and the caller re-issues the cookie).
/// </summary>
public sealed class LocalSessionStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>How stale a session's last extension may get before a request extends it — so a
    /// busy tab does not rewrite the file on every request.</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private readonly ILogger<LocalSessionStore> _logger;
    private readonly Lock _writeLock = new();

    public LocalSessionStore(LocalSettings settings, ILogger<LocalSessionStore> logger, TimeProvider? clock = null)
        : this(Path.Combine(settings.DataDir, "sessions.json"), logger, clock)
    {
    }

    /// <param name="path">Null keeps sessions in memory only (tests).</param>
    public LocalSessionStore(string? path, ILogger<LocalSessionStore> logger, TimeProvider? clock = null)
    {
        _path = path;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        Load();
    }

    /// <summary>Creates a session and returns the raw cookie value (never stored).</summary>
    public string Create()
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = _clock.GetUtcNow();
        _sessions[Hash(token)] = new Session(now + Lifetime, now);
        PurgeExpired(now);
        Persist();
        return token;
    }

    /// <summary>Checks a presented cookie value. <paramref name="refreshed"/> is true when the
    /// session's expiry was just extended, i.e. the caller should send the cookie again.</summary>
    public bool Validate(string? token, out bool refreshed)
    {
        refreshed = false;
        if (string.IsNullOrEmpty(token) || token.Length > 256)
            return false;

        var hash = Hash(token);
        if (!_sessions.TryGetValue(hash, out var session))
            return false;

        var now = _clock.GetUtcNow();
        if (session.ExpiresAt <= now)
        {
            _sessions.TryRemove(hash, out _);
            Persist();
            return false;
        }

        if (now - session.RefreshedAt >= RefreshAfter)
        {
            _sessions[hash] = new Session(now + Lifetime, now);
            Persist();
            refreshed = true;
        }

        return true;
    }

    public void Revoke(string token)
    {
        if (_sessions.TryRemove(Hash(token), out _))
            Persist();
    }

    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var (hash, session) in _sessions)
        {
            if (session.ExpiresAt <= now)
                _sessions.TryRemove(hash, out _);
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
            return;
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredSession>>(File.ReadAllText(_path), Json) ?? [];
            var now = _clock.GetUtcNow();
            foreach (var s in stored)
            {
                if (s.Hash is { Length: 64 } && s.ExpiresAt > now)
                    _sessions[s.Hash] = new Session(s.ExpiresAt, s.RefreshedAt);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt file only costs a sign-in: the launcher mints a new launch code.
            _logger.LogWarning(ex, "Could not read {Path}; starting with no browser sessions", _path);
        }
    }

    private void Persist()
    {
        if (_path is null)
            return;
        lock (_writeLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var snapshot = _sessions
                    .Select(kv => new StoredSession(kv.Key, kv.Value.ExpiresAt, kv.Value.RefreshedAt))
                    .ToList();
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, Json));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not write {Path}; sessions survive until restart only", _path);
            }
        }
    }

    private sealed record Session(DateTimeOffset ExpiresAt, DateTimeOffset RefreshedAt);

    private sealed record StoredSession(string Hash, DateTimeOffset ExpiresAt, DateTimeOffset RefreshedAt);
}
