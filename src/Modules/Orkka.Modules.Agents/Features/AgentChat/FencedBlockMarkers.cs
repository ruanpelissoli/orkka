namespace Orkka.Modules.Agents.Features.AgentChat;

/// <summary>
/// Where a fenced block marker (```document, ```design, …) may legitimately start in an agent
/// reply: at the beginning of a line, followed by the end of the line. A plain substring search
/// also matched the marker INSIDE a JSON string — a consolidated knowledge base quoting the
/// project's own CLAUDE.md ("via ```design blocks") made the document payload look like a design
/// block to both the platform and the panel (2026-09-07: "Creating designs…" under a documents
/// reply, and a design parser diagnosing JSON it was never meant to read).
/// </summary>
internal static class FencedBlockMarkers
{
    /// <summary>Index of the next marker that opens a fence, or -1.</summary>
    public static int IndexOf(string text, string marker, int startIndex)
    {
        var idx = startIndex;
        while ((idx = text.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
        {
            if (OpensFence(text, marker, idx))
                return idx;
            idx += marker.Length;
        }
        return -1;
    }

    /// <summary>True when the marker at <paramref name="idx"/> sits at a line start and ends its
    /// line (allowing trailing spaces) — so "```document" never matches "```documents" and never
    /// matches a mention mid-sentence.</summary>
    public static bool OpensFence(string text, string marker, int idx)
    {
        if (idx > 0 && text[idx - 1] != '\n')
            return false;

        var after = idx + marker.Length;
        while (after < text.Length && (text[after] == ' ' || text[after] == '\t'))
            after++;
        return after >= text.Length || text[after] == '\n' || text[after] == '\r';
    }
}
