namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed record DesignFrameSpec(string? Prompt);

internal sealed record DesignAssetSpec(
    string? Kind,
    string? Name,
    string? Description,
    string? Svg,
    string? Prompt,
    string? AspectRatio,
    string? BasedOnAssetId,
    int? FrameDelayMs,
    List<DesignFrameSpec>? Frames,
    // Chat-attachment ids ([attachment:...] markers) whose images feed the Gemini call as
    // visual references — "make it look like this".
    List<string>? ReferenceAttachmentIds,
    // Existing design-asset ids whose images feed the call as visual references — how a
    // NEW asset shows the same character/object as an approved one (sprite sheets, other
    // facings). Distinct from basedOnAssetId, which versions THIS asset onto a lineage.
    List<string>? ReferenceAssetIds = null,
    // Model-produced aliases, observed in real turns despite the documented schema —
    // silently dropping them broke character consistency. Read via the Effective* members.
    List<string>? References = null,
    List<string>? ReferenceAssets = null,
    string? Aspect = null,
    // More observed aliases: models communicate size as pixel dimensions instead of
    // "aspectRatio" — honored as the nearest documented ratio, never as literal pixels.
    int? Width = null,
    int? Height = null,
    // Asks for a real alpha channel (logos, marks, sprites). Providers that cannot emit
    // transparency are prompted for a flat canvas and it is keyed out afterwards.
    bool? Transparent = null,
    // Observed alias: models mirror the OpenAI Images parameter name instead.
    string? Background = null)
{
    public List<string>? EffectiveReferenceAssetIds => ReferenceAssetIds ?? ReferenceAssets ?? References;

    public string? EffectiveAspectRatio => AspectRatio ?? Aspect ?? NearestAspectRatio(Width, Height);

    public bool EffectiveTransparent => Transparent
        ?? string.Equals(Background?.Trim(), "transparent", StringComparison.OrdinalIgnoreCase);

    // The documented ratio set (keep in sync with DesignAssetGenerator.AllowedAspectRatios)
    // with numeric values for nearest-match mapping of width/height drift.
    private static readonly (string Ratio, double Value)[] RatioTable =
    [
        ("1:1", 1.0), ("2:3", 2.0 / 3), ("3:2", 1.5), ("3:4", 0.75), ("4:3", 4.0 / 3),
        ("4:5", 0.8), ("5:4", 1.25), ("9:16", 9.0 / 16), ("16:9", 16.0 / 9), ("21:9", 21.0 / 9),
    ];

    /// <summary>Maps pixel dimensions to the closest documented aspect ratio (log-scale
    /// distance, so 2:1 is as far from 1:1 as 1:2 is). Null when either side is missing.</summary>
    internal static string? NearestAspectRatio(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0)
            return null;
        var ratio = (double)width.Value / height.Value;
        return RatioTable.MinBy(t => Math.Abs(Math.Log(ratio / t.Value))).Ratio;
    }
}

internal sealed record DesignAssetsBlock(List<DesignAssetSpec>? Assets);

/// <summary>
/// Extracts ```design blocks from a Designer agent response. SVG markup and generation
/// prompts inside the JSON payload routinely contain ``` sequences, so — like
/// DocumentBlockParser — each subsequent fence is tried as the block terminator until
/// the payload parses as JSON.
///
/// Parsing is LENIENT per asset: models drift from the documented schema in real turns
/// (numeric "frames" counts, "fps" instead of "frameDelayMs", pixel "width"/"height",
/// frames as plain strings) and a strict deserializer used to discard the ENTIRE block —
/// including its valid assets — over one bad field. Known drift is normalized; an asset
/// that still fails keeps its name/kind so the generator can fail it visibly instead of
/// silently swallowing it.
/// </summary>
internal static class DesignBlockParser
{
    private const string Marker = "```design";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<DesignAssetsBlock> Parse(string responseText)
        => Parse(responseText, out _);

    /// <summary>Parses all blocks; when the text contains a ```design marker but nothing
    /// usable came out, <paramref name="diagnostics"/> carries a human/model-readable
    /// reason (invalid JSON, missing assets array) for the failure event.</summary>
    public static IReadOnlyList<DesignAssetsBlock> Parse(string responseText, out string? diagnostics)
    {
        var blocks = new List<DesignAssetsBlock>();
        diagnostics = null;
        var idx = 0;

        while ((idx = FencedBlockMarkers.IndexOf(responseText, Marker, idx)) >= 0)
        {
            var newline = responseText.IndexOf('\n', idx + Marker.Length);
            if (newline < 0)
            {
                diagnostics ??= "The ```design block has no content after the marker.";
                break;
            }

            var contentStart = newline + 1;
            var searchFrom = contentStart;
            DesignAssetsBlock? parsed = null;
            var blockEnd = -1;
            string? blockError = null;

            while (true)
            {
                var fence = responseText.IndexOf("```", searchFrom, StringComparison.Ordinal);
                if (fence < 0)
                {
                    blockError ??= "The ```design block is unterminated or not valid JSON.";
                    break;
                }

                var candidate = responseText[contentStart..fence].Trim();
                if (candidate.StartsWith('{'))
                {
                    try
                    {
                        var node = JsonNode.Parse(candidate);
                        parsed = BuildBlock(node, ref blockError);
                        blockEnd = fence;
                        break;
                    }
                    catch (JsonException ex)
                    {
                        // Could be an inner fence — extend the candidate to the next one.
                        // Keep the message in case no later fence ever parses.
                        blockError = $"The ```design block is not valid JSON: {ex.Message}";
                    }
                }
                else
                {
                    blockError ??= "The ```design block payload must be a JSON object.";
                }

                searchFrom = fence + 3;
            }

            if (blockEnd < 0 || parsed is null)
            {
                diagnostics ??= blockError;
                // Unparseable block: skip past the marker so a later valid block is still found.
                idx += Marker.Length;
                continue;
            }

            if (parsed.Assets is not { Count: > 0 })
                diagnostics ??= "The ```design block parsed but contains no \"assets\" array.";

            blocks.Add(parsed);
            idx = blockEnd + 3;
        }

        return blocks;
    }

    /// <summary>Lenient single-payload entry point for the create_design_assets tool —
    /// same drift normalization as the fenced path. Returns null (with diagnostics) only
    /// when the payload is not a JSON object at all.</summary>
    public static DesignAssetsBlock? ParseBlockJson(string json, out string? diagnostics)
    {
        diagnostics = null;
        try
        {
            return BuildBlock(JsonNode.Parse(json), ref diagnostics);
        }
        catch (JsonException ex)
        {
            diagnostics = $"The payload is not valid JSON: {ex.Message}";
            return null;
        }
    }

    private static DesignAssetsBlock? BuildBlock(JsonNode? node, ref string? diagnostics)
    {
        if (node is not JsonObject root)
        {
            diagnostics ??= "The design payload must be a JSON object.";
            return null;
        }

        if (Property(root, "assets") is not JsonArray assets)
            return new DesignAssetsBlock(null);

        var specs = new List<DesignAssetSpec>(assets.Count);
        foreach (var element in assets)
            specs.Add(ParseAsset(element));
        return new DesignAssetsBlock(specs);
    }

    /// <summary>One asset, salvaged as hard as possible: normalize known schema drift,
    /// deserialize, and on failure fall back to a name/kind-only spec so the generator
    /// reports a visible per-asset failure instead of the batch silently shrinking.</summary>
    private static DesignAssetSpec ParseAsset(JsonNode? element)
    {
        if (element is not JsonObject asset)
            return Salvaged(null);

        NormalizeFrames(asset);
        NormalizeFps(asset);

        try
        {
            return asset.Deserialize<DesignAssetSpec>(JsonOptions) ?? Salvaged(asset);
        }
        catch (JsonException)
        {
            return Salvaged(asset);
        }

        static DesignAssetSpec Salvaged(JsonObject? asset) => new(
            Kind: Str(asset, "kind"), Name: Str(asset, "name"), Description: null,
            Svg: Str(asset, "svg"), Prompt: Str(asset, "prompt"), AspectRatio: null,
            BasedOnAssetId: null, FrameDelayMs: null, Frames: null,
            ReferenceAttachmentIds: null);
    }

    /// <summary>Observed drift: "frames": 4 (a count) or "frames": ["...", "..."] (plain
    /// strings) instead of the documented list of { "prompt": ... } objects. A count
    /// becomes N copies of the asset's own prompt — the generator's per-frame wrapper
    /// ("Generate frame i of N…") supplies the motion differentiation.</summary>
    private static void NormalizeFrames(JsonObject asset)
    {
        var key = PropertyKey(asset, "frames");
        if (key is null)
            return;

        switch (asset[key])
        {
            case JsonValue value when value.TryGetValue<int>(out var count):
            {
                var prompt = Str(asset, "prompt") ?? Str(asset, "description");
                if (prompt is null)
                {
                    asset.Remove(key); // kind "gif" then fails with the real "needs frame prompts" error
                    return;
                }
                var frames = new JsonArray();
                for (var i = 0; i < Math.Clamp(count, 2, 8); i++)
                    frames.Add(new JsonObject { ["prompt"] = prompt });
                asset[key] = frames;
                return;
            }
            case JsonArray array:
            {
                for (var i = 0; i < array.Count; i++)
                    if (array[i] is JsonValue v && v.TryGetValue<string>(out var framePrompt))
                        array[i] = new JsonObject { ["prompt"] = framePrompt };
                return;
            }
        }
    }

    /// <summary>Observed drift: "fps" instead of "frameDelayMs" — honored as 1000/fps.</summary>
    private static void NormalizeFps(JsonObject asset)
    {
        var fpsKey = PropertyKey(asset, "fps");
        if (fpsKey is null || PropertyKey(asset, "frameDelayMs") is not null)
            return;

        if (asset[fpsKey] is JsonValue value && value.TryGetValue<double>(out var fps) && fps > 0)
            asset["frameDelayMs"] = (int)Math.Clamp(1000 / fps, 30, 1000);
        asset.Remove(fpsKey);
    }

    private static JsonNode? Property(JsonObject obj, string name)
        => PropertyKey(obj, name) is { } key ? obj[key] : null;

    private static string? PropertyKey(JsonObject obj, string name)
    {
        foreach (var pair in obj)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Key;
        return null;
    }

    private static string? Str(JsonObject? obj, string name)
        => obj is not null && Property(obj, name) is JsonValue value
           && value.TryGetValue<string>(out var s) ? s : null;
}
