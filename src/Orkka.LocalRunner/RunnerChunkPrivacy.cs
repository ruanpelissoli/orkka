namespace Orkka.LocalRunner;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orkka.Modules.Execution.Agents;

/// <summary>
/// What of an agent's stream leaves this machine.
///
/// The host shows the run live — the agent's words, its reasoning, and which tool it is using on
/// which file — and stores the agent's final text as the run's log. It never needs the code itself:
/// - A tool RESULT (file contents read, command output) is dropped here and never crosses the wire.
/// - A tool CALL keeps its name and its small arguments (path, command line, pattern), but loses
///   the bodies it carries (a Write's whole file, an Edit's old and new text, a patch).
///
/// So the only code that ever reaches Orkka's servers is what a person opens on purpose — a diff
/// or a file in the review screen — served live from this machine and not stored there.
/// </summary>
public static class RunnerChunkPrivacy
{
    /// <summary>Argument names whose values are file content rather than a description of the call.</summary>
    internal static readonly HashSet<string> BodyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "content", "contents", "new_string", "old_string", "edits", "new_source", "source",
        "diff", "patch", "unified_diff", "changes", "file_text", "text", "data", "body",
        "replacement", "insert_text", "new_str", "old_str",
    };

    // Readable as written: a path or a command line is shown to a person, not parsed.
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Upper bound for what remains of a tool call's arguments.</summary>
    internal const int MaxToolUseChars = 2_000;

    /// <summary>The chunk as it may leave the machine, or null when it must not leave at all.</summary>
    public static AgentChunk? ForHost(AgentChunk chunk) => chunk.Type switch
    {
        AgentChunkType.ToolResult => null,
        AgentChunkType.ToolUse => chunk with { Content = RedactToolUse(chunk.Content) },
        _ => chunk,
    };

    /// <summary>
    /// "{Tool}: {arguments}" with every body-shaped argument replaced by its size. Arguments that
    /// are not JSON (some CLIs summarise in prose) are cut to <see cref="MaxToolUseChars"/>.
    /// </summary>
    internal static string RedactToolUse(string content)
    {
        var colon = content.IndexOf(':');
        if (colon <= 0)
            return Cap(content);

        var tool = content[..colon];
        var arguments = content[(colon + 1)..].Trim();
        if (arguments.Length == 0 || arguments[0] is not ('{' or '['))
            return Cap(content);

        try
        {
            var node = JsonNode.Parse(arguments);
            if (node is null)
                return Cap(content);
            Strip(node);
            return Cap($"{tool}: {node.ToJsonString(Json)}");
        }
        catch (JsonException)
        {
            return Cap($"{tool}: [arguments withheld]");
        }
    }

    private static void Strip(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var value = obj[key];
                    if (BodyKeys.Contains(key) && value is not null)
                    {
                        var size = value is JsonValue v && v.TryGetValue<string>(out var str)
                            ? str.Length
                            : value.ToJsonString().Length;
                        obj[key] = $"[{size} chars kept on the developer's machine]";
                    }
                    else if (value is not null)
                    {
                        Strip(value);
                    }
                }

                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null)
                        Strip(item);
                }

                break;
        }
    }

    private static string Cap(string s) =>
        s.Length <= MaxToolUseChars ? s : s[..MaxToolUseChars] + "…";
}
