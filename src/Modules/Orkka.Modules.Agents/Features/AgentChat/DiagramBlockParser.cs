namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record DiagramBlock(string Title, string DiagramJson);

internal sealed record DiagramParseResult(
    IReadOnlyList<DiagramBlock> Blocks,
    IReadOnlyList<string> SkipReasons);

/// <summary>
/// Extracts ```excalidraw blocks from an agent response. Plain ```json fences are also
/// accepted when the payload self-identifies as an Excalidraw document; json fences that
/// don't are ordinary code blocks and are ignored without a skip reason. The raw JSON is
/// stored verbatim — skeleton payloads are expanded client-side.
/// </summary>
internal static partial class DiagramBlockParser
{
    private const string DefaultTitle = "Architecture Diagram";

    public static DiagramParseResult Parse(string responseText)
    {
        var blocks = new List<DiagramBlock>();
        var skipReasons = new List<string>();

        foreach (Match match in ExcalidrawBlockRegex().Matches(responseText))
        {
            var fenceLanguage = match.Groups[1].Value;
            var diagramJson = match.Groups[2].Value.Trim();
            var isExplicitFence = fenceLanguage == "excalidraw";

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(diagramJson);
            }
            catch (JsonException ex)
            {
                if (isExplicitFence)
                    skipReasons.Add($"payload is not valid JSON ({ex.Message})");
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (!isExplicitFence && !IsExcalidrawDocument(root))
                    continue; // ordinary ```json code block, not a diagram

                if (TryCreateBlock(root, diagramJson, out var block, out var reason))
                    blocks.Add(block!);
                else
                    skipReasons.Add(reason!);
            }
        }

        return new DiagramParseResult(blocks, skipReasons);
    }

    /// <summary>Validates a diagram payload (shared by the fenced-block path above and the
    /// create_diagram tool): a JSON object with a non-empty "elements" array; "name" becomes
    /// the title. The raw JSON is stored verbatim — skeletons are expanded client-side.</summary>
    public static bool TryCreateBlock(
        JsonElement root, string diagramJson, out DiagramBlock? block, out string? reason)
    {
        block = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            reason = $"root is {root.ValueKind}, expected a JSON object";
            return false;
        }

        if (!root.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
        {
            reason = "missing \"elements\" array";
            return false;
        }

        if (elements.GetArrayLength() == 0)
        {
            reason = "\"elements\" array is empty";
            return false;
        }

        var title = DefaultTitle;
        if (root.TryGetProperty("name", out var nameEl) &&
            nameEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(nameEl.GetString()))
            title = nameEl.GetString()!;

        block = new DiagramBlock(title, diagramJson);
        reason = null;
        return true;
    }

    private static bool IsExcalidrawDocument(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("type", out var typeEl) &&
        typeEl.ValueKind == JsonValueKind.String &&
        typeEl.GetString() == "excalidraw";

    [GeneratedRegex(@"```(excalidraw|json)\s*\n([\s\S]*?)```", RegexOptions.Multiline)]
    private static partial Regex ExcalidrawBlockRegex();
}
