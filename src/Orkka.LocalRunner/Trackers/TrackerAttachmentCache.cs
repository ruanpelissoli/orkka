namespace Orkka.LocalRunner.Trackers;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Ticket attachments, downloaded once with the connection's token into
/// <c>~/.orkka/workspaces/default/tickets/{connectionId}/{ticketKey}/</c> and copied into a run's
/// worktree when the agent starts. Only their metadata ever reaches the host.
///
/// Caps: videos, archives and executables are listed but never downloaded; a file over
/// <see cref="MaxFileBytes"/> is listed but not downloaded; once a ticket's cached files reach
/// <see cref="MaxTicketBytes"/> the rest are listed but not downloaded.
/// </summary>
public sealed class TrackerAttachmentCache(
    string ticketsDir,
    HttpClient http,
    ILogger<TrackerAttachmentCache> logger,
    TimeProvider? timeProvider = null)
{
    public const long MaxFileBytes = 25L * 1024 * 1024;
    public const long MaxTicketBytes = 100L * 1024 * 1024;
    public static readonly TimeSpan PruneAfter = TimeSpan.FromDays(14);

    /// <summary>Written into each ticket folder with the time a poll last listed the ticket.
    /// A sanitized attachment name never starts with a dot, so it cannot collide.</summary>
    internal const string SeenMarker = ".orkka-seen";
    private const int MaxNameLength = 120;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan SeenRefresh = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedRetryAfter = TimeSpan.FromHours(1);

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".3gp",
    };

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // archives and disk images
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".jar", ".war", ".iso", ".dmg", ".cab",
        // executables and installers
        ".exe", ".msi", ".dll", ".com", ".scr", ".app", ".apk", ".ipa", ".deb", ".rpm", ".so", ".dylib", ".pkg", ".bin",
    };

    private static readonly HashSet<string> SkippedMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/zip", "application/x-zip-compressed", "application/x-7z-compressed", "application/x-rar-compressed",
        "application/vnd.rar", "application/gzip", "application/x-gzip", "application/x-tar", "application/x-bzip2",
        "application/x-xz", "application/java-archive", "application/x-apple-diskimage", "application/x-iso9660-image",
        "application/x-msdownload", "application/x-msdos-program", "application/x-msi", "application/x-executable",
        "application/vnd.microsoft.portable-executable", "application/x-sharedlib", "application/vnd.android.package-archive",
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    // A download that failed is not retried on every 30 s poll.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _missingLogged = new(StringComparer.Ordinal);

    public string TicketsDir => ticketsDir;

    public string TicketDir(string connectionId, string ticketKey)
        => Path.Combine(ticketsDir, SafeFileName(connectionId), SafeFileName(ticketKey));

    /// <summary>A file name that is safe on every OS and stays inside its folder: path parts,
    /// reserved characters and Windows device names removed, no leading dot, length capped.</summary>
    public static string SafeFileName(string? name)
    {
        var text = (name ?? "").Replace('\\', '/');
        text = text[(text.LastIndexOf('/') + 1)..];
        var chars = text.Select(ch => ch < 32 || ch == 127 || "<>:\"/\\|?*".Contains(ch) ? '_' : ch).ToArray();
        text = new string(chars).Trim().TrimStart('.', ' ').TrimEnd('.', ' ');
        if (text.Length == 0)
            return "attachment";

        var dot = text.IndexOf('.');
        var stem = dot < 0 ? text : text[..dot];
        if (ReservedWindowsNames.Contains(stem.TrimEnd()))
            text = "_" + text;

        if (text.Length > MaxNameLength)
        {
            var ext = Path.GetExtension(text);
            if (ext.Length > 20)
                ext = "";
            text = text[..(MaxNameLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return text;
    }

    /// <summary>The cache/worktree file name of each attachment, in order: sanitized, and
    /// de-duplicated case-insensitively (<c>shot.png</c>, <c>shot (2).png</c>) so two
    /// attachments with one name never overwrite each other.</summary>
    public static IReadOnlyList<TicketNormalizer.AttachmentName> PlanNames(IReadOnlyList<RawAttachment> attachments)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<TicketNormalizer.AttachmentName>(attachments.Count);
        foreach (var a in attachments)
        {
            var safe = SafeFileName(a.FileName);
            var candidate = safe;
            if (!used.Add(candidate))
            {
                var ext = Path.GetExtension(safe);
                var stem = safe[..^ext.Length];
                for (var n = 2; ; n++)
                {
                    candidate = $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){ext}";
                    if (used.Add(candidate))
                        break;
                }
            }
            names.Add(new TicketNormalizer.AttachmentName(a.Id, a.FileName, candidate, a.DownloadUrl));
        }
        return names;
    }

    /// <summary>Why an attachment is never downloaded, or null.</summary>
    public static string? SkipReason(RawAttachment attachment)
    {
        var mime = attachment.MimeType?.Split(';')[0].Trim() ?? "";
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || VideoExtensions.Contains(Path.GetExtension(attachment.FileName ?? "")))
            return "video";
        if (SkippedExtensions.Contains(Path.GetExtension(attachment.FileName ?? "")) || SkippedMimeTypes.Contains(mime))
            return "type";
        if (attachment.Size > MaxFileBytes)
            return "size";
        return null;
    }

    /// <summary>
    /// Makes the cache folder match the ticket's attachments: downloads what is missing or
    /// whose size changed, deletes files the ticket no longer has, and answers the metadata
    /// the host stores. A failed download is not a failed poll — the file is listed with
    /// <c>Cached=false</c> and retried after an hour.
    /// </summary>
    public async Task<TrackerAttachmentMeta[]> EnsureAsync(
        TrackerCredential credential, string connectionId, string ticketKey,
        IReadOnlyList<RawAttachment> attachments, IReadOnlyList<TicketNormalizer.AttachmentName> names,
        CancellationToken ct)
    {
        var dir = TicketDir(connectionId, ticketKey);
        var metas = new TrackerAttachmentMeta[attachments.Count];
        long used = 0;

        for (var i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i];
            var name = names[i].FileName;
            var cached = false;

            if (SkipReason(a) is null)
            {
                var path = Path.Combine(dir, name);
                var existing = File.Exists(path) ? new FileInfo(path).Length : -1;
                if (existing >= 0 && (a.Size <= 0 || existing == a.Size) && used + existing <= MaxTicketBytes)
                {
                    cached = true;
                    used += existing;
                }
                else if (used + Math.Max(a.Size, 0) <= MaxTicketBytes)
                {
                    var written = await TryDownloadAsync(credential, connectionId, ticketKey, a, dir, path,
                        Math.Min(MaxFileBytes, MaxTicketBytes - used), ct);
                    if (written >= 0)
                    {
                        cached = true;
                        // The declared size counts when larger: the budget is planned from what
                        // the tracker lists, so the same files are cached on every machine.
                        used += Math.Max(written, a.Size);
                    }
                }
            }
            metas[i] = new TrackerAttachmentMeta(name, a.Size, a.MimeType, cached);
        }

        RemoveStale(dir, metas.Where(m => m.Cached).Select(m => m.FileName));
        return metas;
    }

    /// <summary>Records that a poll still lists the ticket, so <see cref="Prune"/> keeps its
    /// folder. Rewritten at most hourly; a ticket without cached files has no folder to keep.</summary>
    public void MarkSeen(string connectionId, string ticketKey)
    {
        var dir = TicketDir(connectionId, ticketKey);
        if (!Directory.Exists(dir))
            return;
        var marker = Path.Combine(dir, SeenMarker);
        var now = _time.GetUtcNow();
        try
        {
            if (ReadSeen(marker) is { } seen && now - seen < SeenRefresh)
                return;
            File.WriteAllText(marker, now.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (IOException ex)
        {
            logger.LogDebug("Could not mark ticket {TicketKey} as seen: {Error}", ticketKey, ex.Message);
        }
    }

    /// <summary>Deletes the folders of tickets no poll has listed for <see cref="PruneAfter"/>.
    /// Returns how many went.</summary>
    public int Prune()
    {
        if (!Directory.Exists(ticketsDir))
            return 0;
        var now = _time.GetUtcNow();
        var removed = 0;
        foreach (var connectionDir in SafeEnumerate(ticketsDir))
        {
            foreach (var ticketDir in SafeEnumerate(connectionDir))
            {
                var seen = ReadSeen(Path.Combine(ticketDir, SeenMarker))
                    ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(ticketDir), TimeSpan.Zero);
                if (now - seen < PruneAfter)
                    continue;
                try
                {
                    Directory.Delete(ticketDir, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug("Could not prune {Dir}: {Error}", ticketDir, ex.Message);
                }
            }
            try
            {
                if (!Directory.EnumerateFileSystemEntries(connectionDir).Any())
                    Directory.Delete(connectionDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next prune.
            }
        }
        return removed;
    }

    /// <summary>
    /// Copies a ticket's cached attachments into <c>{worktree}/.orkka/context/attachments/</c>
    /// (same names overwritten). <c>.orkka/</c> is in every task clone's info/exclude, so the
    /// files are never committed. Returns the number copied, or null when this machine has no
    /// cache for the ticket (logged once per ticket).
    /// </summary>
    public int? CopyToWorktree(string connectionId, string ticketKey, string worktreePath)
    {
        var source = TicketDir(connectionId, ticketKey);
        var files = Directory.Exists(source)
            ? Directory.EnumerateFiles(source).Where(f => !Path.GetFileName(f).StartsWith('.')).ToList()
            : [];
        if (files.Count == 0)
        {
            if (_missingLogged.TryAdd($"{connectionId}|{ticketKey}", 0))
                logger.LogInformation("No cached attachments for {TicketKey} on this machine — the run starts without them.", ticketKey);
            return null;
        }

        var target = Path.Combine(worktreePath, ".orkka", "context", "attachments");
        Directory.CreateDirectory(target);
        var copied = 0;
        foreach (var file in files)
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            copied++;
        }
        return copied;
    }

    private async Task<long> TryDownloadAsync(TrackerCredential credential, string connectionId, string ticketKey,
        RawAttachment a, string dir, string path, long limit, CancellationToken ct)
    {
        var failKey = $"{connectionId}|{ticketKey}|{a.Id}|{a.Size}|{a.FileName}";
        var now = _time.GetUtcNow();
        if (_failed.TryGetValue(failKey, out var failedAt) && now - failedAt < FailedRetryAfter)
            return -1;

        if (!Uri.TryCreate(a.DownloadUrl, UriKind.Absolute, out var url) || !IsOnSite(credential, url))
        {
            // The token is only ever sent to the connection's own site.
            _failed[failKey] = now;
            logger.LogDebug("Attachment of {TicketKey} is not on the tracker site — not downloaded.", ticketKey);
            return -1;
        }

        var temp = Path.Combine(dir, $".orkka-download-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            using var response = await SendFollowingRedirectsAsync(credential, url, ct);
            if (response is null || response.StatusCode != HttpStatusCode.OK)
                throw new IOException($"HTTP {(int?)response?.StatusCode}");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            // Azure DevOps answers a refused PAT with its HTML sign-in page.
            if (mediaType is "text/html" && !LooksLikeHtml(a))
                throw new IOException("the tracker answered with a web page instead of the file");
            if (response.Content.Headers.ContentLength is { } declared && declared > limit)
                throw new IOException("larger than the cache allows");

            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > limit)
                        throw new IOException("larger than the cache allows");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            File.Move(temp, path, overwrite: true);
            _failed.TryRemove(failKey, out _);
            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _failed[failKey] = now;
            logger.LogDebug("Attachment {AttachmentId} of {TicketKey} not cached: {Error}",
                a.Id, ticketKey, credential.Scrub(ex.Message));
            return -1;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>The runner's HttpClient follows no redirects (see <see cref="TrackerHttp"/>),
    /// and Jira answers an attachment with a redirect to its media service. Followed here,
    /// with the Authorization header kept only while the request stays on the tracker's host.</summary>
    private async Task<HttpResponseMessage?> SendFollowingRedirectsAsync(TrackerCredential credential, Uri url, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (IsOnSite(credential, current))
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential.BasicValue);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Headers.UserAgent.ParseAdd("orkka");
            HttpResponseMessage response;
            using (request)
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme != Uri.UriSchemeHttps && current.Scheme != Uri.UriSchemeHttp)
                    return null;
                continue;
            }
            return response;
        }
        return null;
    }

    private static bool IsOnSite(TrackerCredential credential, Uri url)
        => Uri.TryCreate(credential.SiteUrl, UriKind.Absolute, out var site)
            && string.Equals(site.Host, url.Host, StringComparison.OrdinalIgnoreCase)
            && url.Scheme == site.Scheme;

    private static bool LooksLikeHtml(RawAttachment a)
        => a.MimeType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
            || Path.GetExtension(a.FileName) is ".html" or ".htm";

    private void RemoveStale(string dir, IEnumerable<string> keep)
    {
        if (!Directory.Exists(dir))
            return;
        var keepSet = keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.') || keepSet.Contains(name))
                continue;
            TryDelete(file);
        }
    }

    private static DateTimeOffset? ReadSeen(string marker)
    {
        try
        {
            return File.Exists(marker)
                && DateTimeOffset.TryParse(File.ReadAllText(marker).Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at)
                ? at
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; the next poll tries again.
        }
    }
}
