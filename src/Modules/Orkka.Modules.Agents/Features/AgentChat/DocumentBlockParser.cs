namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Text.Json;

internal sealed record AgentDocumentSpec(string? Title, string? Path, string? Content);

internal sealed record AgentDocumentsBlock(List<AgentDocumentSpec>? Documents);

/// <summary>
/// Extracts ```document blocks from an agent response. Document content is markdown and
/// routinely contains its own ``` code fences inside the JSON payload, so a non-greedy
/// regex would terminate the block at the first inner fence. Instead, each subsequent
/// fence is tried as the block terminator until the payload parses as JSON.
/// </summary>
internal static class DocumentBlockParser
{
    private const string Marker = "```document";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<AgentDocumentsBlock> Parse(string responseText)
        => Parse(responseText, out _);

    /// <summary>
    /// Parses every complete block and, when the LAST block never terminated, salvages the
    /// documents that were complete inside it. A reply that hit the model's output ceiling
    /// mid-block used to save nothing at all: on 2026-09-07 a 26-document knowledge base was
    /// cut at 64K output tokens and an eighteen-minute turn produced zero documents, although
    /// most of them were fully written. <paramref name="truncatedDocuments"/> is how many
    /// documents came from that salvage (0 when every block was whole) so the caller can tell
    /// the user what was and was not saved.
    /// </summary>
    public static IReadOnlyList<AgentDocumentsBlock> Parse(string responseText, out int truncatedDocuments)
    {
        truncatedDocuments = 0;
        var blocks = new List<AgentDocumentsBlock>();
        var idx = 0;

        while ((idx = FencedBlockMarkers.IndexOf(responseText, Marker, idx)) >= 0)
        {
            var newline = responseText.IndexOf('\n', idx + Marker.Length);
            if (newline < 0)
                break; // marker at end of text with no payload

            var contentStart = newline + 1;
            var searchFrom = contentStart;
            AgentDocumentsBlock? parsed = null;
            var blockEnd = -1;

            while (true)
            {
                var fence = responseText.IndexOf("```", searchFrom, StringComparison.Ordinal);
                if (fence < 0)
                    break; // unterminated block

                var candidate = responseText[contentStart..fence].Trim();
                if (candidate.StartsWith('{'))
                {
                    try
                    {
                        parsed = JsonSerializer.Deserialize<AgentDocumentsBlock>(candidate, JsonOptions);
                        if (parsed is not null)
                        {
                            blockEnd = fence;
                            break;
                        }
                    }
                    catch (JsonException)
                    {
                        // Inner fence — extend the candidate to the next one.
                    }
                }

                searchFrom = fence + 3;
            }

            if (blockEnd < 0 || parsed is null)
            {
                // An unterminated block is only ever the LAST one (a later marker would have
                // served as its terminator search space): if it holds whole documents, keep
                // them. An unparseable but terminated block is skipped as before.
                var salvaged = SalvageTruncated(responseText[contentStart..]);
                if (salvaged is { Documents.Count: > 0 })
                {
                    blocks.Add(salvaged);
                    truncatedDocuments = salvaged.Documents!.Count;
                    break;
                }

                // Unparseable block: skip past the marker so a later valid block is still found.
                idx += Marker.Length;
                continue;
            }

            blocks.Add(parsed);
            idx = blockEnd + 3;
        }

        return blocks;
    }

    /// <summary>
    /// The complete elements of a cut-off <c>{"documents": [ … </c> payload: walks the JSON
    /// with string and escape awareness, collects every top-level array element that closed,
    /// and re-parses them as a block. Anything after the last closed element — the document
    /// the ceiling landed in — is dropped, never guessed at.
    /// </summary>
    internal static AgentDocumentsBlock? SalvageTruncated(string payload)
    {
        var arrayStart = payload.IndexOf('[');
        if (arrayStart < 0 || !payload[..arrayStart].Contains("\"documents\"", StringComparison.OrdinalIgnoreCase))
            return null;

        var elements = new List<string>();
        var depth = 0;
        var inString = false;
        var escaped = false;
        var elementStart = -1;

        for (var i = arrayStart + 1; i < payload.Length; i++)
        {
            var c = payload[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0) elementStart = i;
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0 && elementStart >= 0)
                    {
                        elements.Add(payload[elementStart..(i + 1)]);
                        elementStart = -1;
                    }
                    break;
                case ']':
                    if (depth == 0)
                        i = payload.Length; // the array closed — nothing was truncated here
                    break;
            }
        }

        if (elements.Count == 0)
            return null;

        try
        {
            return JsonSerializer.Deserialize<AgentDocumentsBlock>(
                "{\"documents\":[" + string.Join(",", elements) + "]}", JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
