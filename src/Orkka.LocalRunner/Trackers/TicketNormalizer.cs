namespace Orkka.LocalRunner.Trackers;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// Turns a tracker's ticket into what the host stores and agents read: markdown instead of
/// HTML, inline images rewritten to the attachment file names the run will find under
/// <c>.orkka/context/attachments/</c>, scripts and styles gone, sizes capped — and a content
/// hash over exactly the parts that make a ticket "changed".
/// </summary>
public static partial class TicketNormalizer
{
    public const int MaxDescriptionChars = 20_000;
    public const int MaxCommentChars = 4_000;
    public const string TruncatedSuffix = "…(truncated)";

    // Tags whose content is never ticket text.
    private const string DroppedTags = "script, style, noscript, iframe, object, embed, template, link, meta";

    private static readonly HtmlParser Parser = new();

    /// <summary>An attachment as the normalizer needs it: how the tracker refers to it in
    /// HTML (<paramref name="Id"/>, <paramref name="Url"/>, <paramref name="OriginalName"/>)
    /// and the file name it has in the cache and the worktree (<paramref name="FileName"/>).</summary>
    public sealed record AttachmentName(string Id, string OriginalName, string FileName, string? Url);

    /// <summary>The ticket as the host gets it. <see cref="TrackerTicket.Body"/> is null when
    /// the hash equals <paramref name="knownHash"/>; comments are passed in already fetched.</summary>
    public static TrackerTicket Normalize(
        RawTicket raw,
        IReadOnlyList<AttachmentName> names,
        TrackerAttachmentMeta[] attachments,
        string? knownHash,
        IReadOnlyList<RawComment> newComments)
    {
        var description = Cap(ToMarkdown(raw.DescriptionHtml, names), MaxDescriptionChars);
        var acceptance = NullIfEmpty(Cap(ToMarkdown(raw.AcceptanceCriteriaHtml, names), MaxDescriptionChars));
        var repro = NullIfEmpty(Cap(ToMarkdown(raw.ReproStepsHtml, names), MaxDescriptionChars));
        var title = CollapseInline(raw.Title);
        var labels = raw.Labels.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

        var hash = ContentHash(title, raw.Type, raw.Priority, description, acceptance, repro,
            labels, attachments.Select(a => a.FileName));

        var body = string.Equals(hash, knownHash, StringComparison.Ordinal)
            ? null
            : new TrackerTicketBody(title, raw.Type, raw.Status, raw.StatusCategory, raw.Priority,
                description, acceptance, repro, labels, attachments, raw.UpdatedAt,
                raw.Components.Length > 0 ? raw.Components : null, raw.AreaPath);

        var comments = newComments
            .Select(c => new TrackerTicketComment(c.Id, c.Author, c.CreatedAt,
                Cap(ToMarkdown(c.BodyHtml, names), MaxCommentChars)))
            .ToArray();

        // Status, assignment, intake and the hierarchy travel on every report — the host tracks
        // a ticket through In Progress and beyond, and retires or revives on these alone.
        return new TrackerTicket(raw.Key, raw.Id, raw.Url, raw.EpicKey, hash, body, comments,
            raw.Status, raw.StatusCategory, raw.AssignedToMe, raw.InIntake,
            raw.ParentKey, raw.ParentType, raw.ChildKeys);
    }

    /// <summary>SHA-256 (lowercase hex) over the normalized title, type, priority, description,
    /// acceptance criteria, repro steps, labels and attachment names — NOT the comments (they
    /// travel separately, newer-than), NOT the updated time (a comment or a watcher bumps it
    /// without changing what the ticket asks for) and NOT the status: status travels on every
    /// report anyway, and a hash that moved with it made every status change — Orkka's own
    /// write-back included — read as "the ticket was edited" (a rewrite of the task, or a
    /// "was updated in Jira" note on started work). Labels and attachment names are
    /// order-insensitive.</summary>
    public static string ContentHash(
        string title, string? type, string? priority,
        string description, string? acceptanceCriteria, string? reproSteps,
        IEnumerable<string> labels, IEnumerable<string> attachmentNames)
    {
        var parts = new[]
        {
            "v2",
            title,
            type ?? "",
            priority ?? "",
            description,
            acceptanceCriteria ?? "",
            reproSteps ?? "",
            string.Join("\n", labels.Order(StringComparer.Ordinal)),
            string.Join("\n", attachmentNames.Order(StringComparer.Ordinal)),
        };
        // U+001F (unit separator) between fields, so "ab"+"c" never hashes like "a"+"bc".
        var bytes = Encoding.UTF8.GetBytes(string.Join("\u001f", parts));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>HTML → markdown. Inline images that are one of the ticket's attachments become
    /// <c>[attachment: file name]</c>; other absolute images stay as markdown images; relative
    /// or data: images (emoticons, pasted blobs) keep only their alt text.</summary>
    public static string ToMarkdown(string? html, IReadOnlyList<AttachmentName>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var doc = Parser.ParseDocument(html);
        var body = doc.Body;
        if (body is null)
            return "";
        foreach (var element in body.QuerySelectorAll(DroppedTags).ToList())
            element.Remove();

        // Placeholders, substituted after conversion: a file name like my_file.png must not
        // come out as my\_file.png, and the converter would escape it.
        var placeholders = new List<string>();
        foreach (var img in body.QuerySelectorAll("img").ToList())
        {
            var replacement = ImageReplacement(img, attachments);
            if (replacement is null)
            {
                img.Remove();
                continue;
            }
            var token = $"ORKKAPLACEHOLDER{placeholders.Count}X";
            placeholders.Add(replacement);
            img.Replace(doc.CreateTextNode(token));
        }

        var converter = new ReverseMarkdown.Converter(CreateConfig());
        string markdown;
        try
        {
            markdown = converter.Convert(body.InnerHtml);
        }
        catch (Exception)
        {
            // A converter bug must not lose a ticket: plain text is still the ticket.
            markdown = body.TextContent;
        }

        for (var i = placeholders.Count - 1; i >= 0; i--)
            markdown = markdown.Replace($"ORKKAPLACEHOLDER{i}X", placeholders[i], StringComparison.Ordinal);

        return CollapseWhitespace(markdown);
    }

    /// <summary>Cuts <paramref name="text"/> to <paramref name="max"/> characters (never inside
    /// a surrogate pair) and says so.</summary>
    public static string Cap(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut].TrimEnd() + "\n" + TruncatedSuffix;
    }

    private static ReverseMarkdown.Config CreateConfig()
    {
        var config = new ReverseMarkdown.Config
        {
            GithubFlavored = true,
        };
        config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Formatting.OutputLineEnding = "\n";
        config.Images.Base64Handling = ReverseMarkdown.Config.Base64ImageHandling.Skip;
        config.Tables.CellListHandling = ReverseMarkdown.Config.TableCellListHandlingOption.InlineText;
        config.Preprocess.RemoveScripts().RemoveStyles();
        return config;
    }

    private static string? ImageReplacement(IElement img, IReadOnlyList<AttachmentName>? attachments)
    {
        var src = img.GetAttribute("src")?.Trim() ?? "";
        var alt = CollapseInline(img.GetAttribute("alt") ?? "");

        if (attachments is { Count: > 0 } && MatchAttachment(src, alt, attachments) is { } match)
            return $"[attachment: {match.FileName}]";

        if (Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return $"![{EscapeAlt(alt)}]({src.Replace(" ", "%20", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal)})";

        return alt.Length > 0 ? alt : null;
    }

    /// <summary>Jira renders an attachment as <c>/rest/api/3/attachment/content/10001</c>,
    /// <c>/secure/attachment/10001/name.png</c> or a thumbnail URL; Azure DevOps as
    /// <c>…/_apis/wit/attachments/{guid}?fileName=name.png</c>. Matched by id inside an
    /// attachment path, by the exact URL, then by the file name.</summary>
    internal static AttachmentName? MatchAttachment(string src, string alt, IReadOnlyList<AttachmentName> attachments)
    {
        if (src.Length == 0)
            return null;
        var decoded = SafeUnescape(src);
        var pathPart = decoded.Split('?', '#')[0];
        var query = decoded.Contains('?', StringComparison.Ordinal) ? decoded[(decoded.IndexOf('?', StringComparison.Ordinal) + 1)..] : "";
        var lastSegment = pathPart.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        var queryFileName = FileNameParam().Match(query) is { Success: true } m ? m.Groups[1].Value : null;
        var isAttachmentPath = pathPart.Contains("attachment", StringComparison.OrdinalIgnoreCase);

        foreach (var a in attachments)
        {
            if (a.Url is { Length: > 0 } url
                && string.Equals(SafeUnescape(url).Split('?')[0].TrimEnd('/'), pathPart.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return a;
        }
        foreach (var a in attachments)
        {
            if (isAttachmentPath && a.Id.Length > 0
                && (pathPart.Contains($"/{a.Id}/", StringComparison.OrdinalIgnoreCase)
                    || pathPart.TrimEnd('/').EndsWith($"/{a.Id}", StringComparison.OrdinalIgnoreCase)))
                return a;
        }
        foreach (var a in attachments)
        {
            if (string.Equals(queryFileName, a.OriginalName, StringComparison.OrdinalIgnoreCase)
                || (isAttachmentPath && string.Equals(lastSegment, a.OriginalName, StringComparison.OrdinalIgnoreCase))
                || (isAttachmentPath && alt.Length > 0 && string.Equals(alt, a.OriginalName, StringComparison.OrdinalIgnoreCase)))
                return a;
        }
        return null;
    }

    private static string SafeUnescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    private static string EscapeAlt(string alt) => alt.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);

    /// <summary>Non-breaking spaces become spaces, trailing spaces go, runs of blank lines
    /// collapse to one — outside fenced code blocks, whose whitespace is content.</summary>
    internal static string CollapseWhitespace(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(markdown.Length);
        var inFence = false;
        var blankRun = 0;
        foreach (var rawLine in lines)
        {
            if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;
            var line = inFence ? rawLine.TrimEnd() : rawLine.Replace(' ', ' ').TrimEnd();
            if (!inFence && line.Length == 0)
            {
                blankRun++;
                if (blankRun > 1)
                    continue;
            }
            else
            {
                blankRun = 0;
            }
            output.Append(line).Append('\n');
        }
        return output.ToString().Trim('\n', ' ');
    }

    private static string CollapseInline(string text) => InlineSpace().Replace(text.Replace(' ', ' '), " ").Trim();

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    [GeneratedRegex(@"(?:^|&)fileName=([^&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameParam();

    [GeneratedRegex(@"\s+")]
    private static partial Regex InlineSpace();
}
