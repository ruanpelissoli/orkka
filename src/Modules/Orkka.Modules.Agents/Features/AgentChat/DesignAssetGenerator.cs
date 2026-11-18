namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Runtime.CompilerServices;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Usage.Contracts;
using Orkka.SharedKernel.Ai;
using Orkka.SharedKernel.Storage;

public abstract record DesignGenerationEvent;

/// <summary>Emitted before each asset is persisted (Frame/TotalFrames are always null now that
/// GIFs are gone; the SSE shape is kept for clients).</summary>
public sealed record DesignAssetProgress(string Name, string Kind, int? Frame, int? TotalFrames) : DesignGenerationEvent;

public sealed record DesignAssetReady(DesignAsset Asset, string Url) : DesignGenerationEvent;

public sealed record DesignAssetFailed(string Name, string Kind, string Error) : DesignGenerationEvent;

/// <summary>Maps generation events to the SSE payload shapes the chat client renders —
/// shared by the post-stream fenced-block path (ChatTurnRunner) and the
/// create_design_assets tool handler so both emit identical events.</summary>
public static class DesignEventSse
{
    public static object? ToPayload(DesignGenerationEvent evt) => evt switch
    {
        DesignAssetProgress p => new
        {
            designAssetProgress = new
            {
                name = p.Name,
                kind = p.Kind,
                status = "generating",
                frame = p.Frame,
                totalFrames = p.TotalFrames
            }
        },
        DesignAssetReady r => new
        {
            designAsset = (object)new
            {
                id = r.Asset.Id.ToString(),
                name = r.Asset.Name,
                kind = r.Asset.Kind.ToString().ToLowerInvariant(),
                version = r.Asset.Version,
                url = r.Url
            }
        },
        DesignAssetFailed f => new
        {
            designAsset = (object)new
            {
                name = f.Name,
                kind = f.Kind,
                status = "failed",
                error = f.Error
            }
        },
        _ => null
    };
}

/// <summary>
/// Turns parsed ```design block entries into persisted DesignAsset rows + blobs. Every
/// generated asset is an SVG: the agent authors the markup itself on the developer's runner,
/// and it is validated and uploaded here. Orkka's servers never call an image model — there
/// is no raster or GIF generation. A legacy kind "image" request with only a prompt is
/// answered on the post-stream fenced path by an SVG drawn on the requester's own runner
/// (<see cref="IUserRunnerUtility"/>, $0); on tool turns the still-running agent is told to
/// re-deliver it as SVG itself. Uploads are billed as storage through
/// <see cref="IUsageRecorder"/>.
/// </summary>
public sealed class DesignAssetGenerator(
    AgentsDbContext db,
    IUserRunnerUtility runnerUtility,
    IOptions<AnthropicOptions> anthropicOptions,
    IBlobStorageService blobStorage,
    IUsageRecorder usageRecorder,
    ILogger<DesignAssetGenerator> logger)
{
    public const string BlobContainerName = "design-assets";
    /// <summary>
    /// How long a signed link to an asset stays valid.
    ///
    /// This is not a security window, it is a page's lifetime: the URL is baked into an
    /// &lt;img&gt; on the Design page and into chat history, and a browser tab outlives any
    /// short signature. At 15 minutes every image on a page left open turned into Azure's
    /// raw XML ("AuthenticationFailed ... signed expiry time must be after signed start
    /// time"), which reads as "the images were never generated" -- exactly the wrong thing
    /// to tell someone whose assets are sitting safely in blob storage (2026-09-08).
    ///
    /// A working session is the unit that matters, so the link outlives one. The tokens are
    /// read-only, scoped to a single blob, and the assets are the org's own.
    /// </summary>
    public static readonly TimeSpan DisplayUrlExpiry = TimeSpan.FromHours(12);

    private const int MaxAssetsPerMessage = 4;
    private const int MaxPromptChars = 4000;
    private const int MaxSvgBytes = 200 * 1024;
    private const string SvgSourceModel = "claude-chat";

    /// <summary>What an agent is told when it asks for something other than SVG.</summary>
    internal const string SvgOnlyMessage =
        "Only SVG assets are supported — re-deliver this asset as kind \"svg\" with complete " +
        "SVG markup you author yourself (same name). An animation can be an animated SVG " +
        "(<animate> or CSS keyframes inside the SVG).";

    private static readonly HashSet<string> AllowedAspectRatios =
        ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"];

    /// <summary>Parses ```design blocks out of a raw agent response and generates the assets
    /// (legacy fenced-block path — the create_design_assets tool calls the spec overload).</summary>
    public async IAsyncEnumerable<DesignGenerationEvent> GenerateAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, Ulid conversationId, Ulid messageId,
        string requesterUserId, string responseText,
        string? defaultFolderPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var specs = DesignBlockParser.Parse(responseText, out var parseDiagnostics)
            .Where(b => b.Assets is { Count: > 0 })
            .SelectMany(b => b.Assets!)
            .ToList();

        if (specs.Count == 0 && responseText.Contains("```design", StringComparison.Ordinal))
        {
            // On this path nothing reaches the (already finished) agent, so the failure
            // event must carry the actual reason — a generic banner sent Sprite into a
            // blind retry loop emitting the same invalid block.
            var reason = parseDiagnostics ?? "The ```design block could not be parsed.";
            logger.LogWarning(
                "Message {MessageId} contains a design block that failed to parse: {Reason}",
                messageId, reason);
            yield return new DesignAssetFailed("design-block", "block", reason);
            yield break;
        }

        await foreach (var evt in GenerateAsync(orgId, projectId, agentId, conversationId,
            messageId, requesterUserId, specs, modelCanRetry: false, defaultFolderPath, ct))
            yield return evt;
    }

    /// <summary>Persists assets from already-parsed specs, streaming progress / ready /
    /// failed events. Specs are raw agent output — everything is validated here.
    /// (Internal because DesignAssetSpec is — callers outside the module use the string overload.)
    /// modelCanRetry: true on tool turns, where a non-SVG request is reported back to the
    /// still-running agent so IT authors the SVG — the agent is already a capable SVG
    /// author, and a separate runner generation inside a tool call would stall the
    /// in-flight turn for its full duration. The runner SVG drawing is reserved for the
    /// post-stream fenced path, where nothing is waiting on it.</summary>
    internal async IAsyncEnumerable<DesignGenerationEvent> GenerateAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, Ulid conversationId, Ulid messageId,
        string requesterUserId, IReadOnlyList<DesignAssetSpec> specsInput,
        bool modelCanRetry = false,
        string? defaultFolderPath = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var specs = specsInput.ToList();
        if (specs.Count == 0)
            yield break;

        // The runner SVG drawing gets ONE shot per batch: if it fails (or times out at its
        // 3-minute cap), retrying it for every remaining asset multiplies a dead wait.
        var svgFallbackAvailable = true;

        if (specs.Count > MaxAssetsPerMessage)
        {
            logger.LogWarning(
                "Design block in message {MessageId} has {Count} assets — capping at {Max}",
                messageId, specs.Count, MaxAssetsPerMessage);
            specs = specs.Take(MaxAssetsPerMessage).ToList();
        }

        foreach (var spec in specs)
        {
            var name = NormalizeName(spec.Name);
            var kindLabel = spec.Kind?.Trim().ToLowerInvariant() ?? "";

            if (name is null || kindLabel is not ("svg" or "image" or "gif"))
            {
                yield return new DesignAssetFailed(
                    spec.Name ?? "(unnamed)", kindLabel, "Invalid asset name or kind.");
                continue;
            }

            DesignAsset? parent = null;
            if (spec.BasedOnAssetId is not null)
            {
                if (!Ulid.TryParse(spec.BasedOnAssetId, out var parentId))
                {
                    yield return new DesignAssetFailed(name, kindLabel, "Invalid basedOnAssetId.");
                    continue;
                }

                parent = await db.DesignAssets.AsNoTracking().FirstOrDefaultAsync(
                    a => a.Id == parentId && a.OrgId == orgId && a.ProjectId == projectId, ct);

                if (parent is null)
                {
                    yield return new DesignAssetFailed(
                        name, kindLabel, "basedOnAssetId does not match an asset in this project.");
                    continue;
                }
            }
            else
            {
                // Name-based lineage auto-link: agents routinely forget basedOnAssetId when
                // iterating, which minted duplicate gallery roots. Delivering an asset under
                // an existing name IS an iteration — version it onto the newest asset of
                // that name instead. (The gallery shows one card per lineage.)
                parent = await db.DesignAssets.AsNoTracking()
                    .Where(a => a.OrgId == orgId && a.ProjectId == projectId && a.Name == name)
                    .OrderByDescending(a => a.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (parent is not null)
                    logger.LogInformation(
                        "Design asset \"{Name}\" delivered without basedOnAssetId — versioning onto existing lineage {RootAssetId}",
                        name, parent.RootAssetId);
            }

            yield return new DesignAssetProgress(name, kindLabel, Frame: null, TotalFrames: null);

            (byte[] Data, string MimeType)? content = null;
            string? failure = null;

            if (kindLabel == "svg")
            {
                (content, failure) = BuildSvg(spec);
            }
            else if (kindLabel == "image" && !modelCanRetry && svgFallbackAvailable
                && !string.IsNullOrWhiteSpace(spec.Prompt))
            {
                // A legacy fenced "image" request: nothing is waiting on this path, so the
                // requester's own runner draws an SVG from the prompt.
                var (svgContent, svgFailure) = await DrawSvgOnRunnerAsync(
                    orgId, projectId, agentId, requesterUserId, spec, ct);
                if (svgContent is not null)
                {
                    content = svgContent;
                    logger.LogInformation(
                        "Design asset {Name} requested as an image — delivered a runner-drawn SVG", name);
                }
                else
                {
                    svgFallbackAvailable = false;
                    failure = svgFailure ?? SvgOnlyMessage;
                }
            }
            else
            {
                failure = SvgOnlyMessage;
            }

            if (content is null)
            {
                yield return new DesignAssetFailed(name, kindLabel, failure ?? "Generation failed.");
                continue;
            }

            var asset = await PersistAsync(
                orgId, projectId, agentId, conversationId, messageId, requesterUserId,
                name, content.Value.Data, content.Value.MimeType, parent, defaultFolderPath, ct);

            var url = await blobStorage.GenerateDownloadUrlAsync(
                asset.BlobContainer, asset.BlobName, DisplayUrlExpiry);

            yield return new DesignAssetReady(asset, url);
        }
    }

    private static ((byte[] Data, string MimeType)? Content, string? Failure) BuildSvg(DesignAssetSpec spec)
    {
        var svg = spec.Svg?.Trim();
        if (string.IsNullOrEmpty(svg) || !svg.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return (null, "SVG assets must include inline markup starting with <svg.");

        var bytes = Encoding.UTF8.GetBytes(svg);
        if (bytes.Length > MaxSvgBytes)
            return (null, $"SVG exceeds the {MaxSvgBytes / 1024} KB limit.");

        return ((bytes, "image/svg+xml"), null);
    }

    private const int SvgFallbackMaxTokens = 8192;

    /// <summary>Draws a self-contained SVG from a legacy image prompt on the requester's own
    /// runner. Returns (null, reason) when it cannot.</summary>
    private async Task<((byte[] Data, string MimeType)? Content, string? Failure)> DrawSvgOnRunnerAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, string requesterUserId,
        DesignAssetSpec spec, CancellationToken ct)
    {
        var prompt = spec.Prompt!.Trim();

        // Cap far below the runner's generic 15-minute op ceiling so a wedged call fails
        // an asset, not the user's whole afternoon. The budget must absorb the WORST case —
        // a CLI-spawned generation plus the runner's automatic one-retry (measured: a sonnet
        // SVG takes ~2.5 min per attempt, which is why this call uses the fast utility model
        // below: ~30-60s per attempt keeps 2× under this cap).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            // Fast utility tier, not the standard one: "an asset arrives" beats "the best
            // possible asset never arrives". The standard model's ~2.5-minute CLI
            // generations outran this cap and died as "Invocation canceled by the server".
            var model = anthropicOptions.Value.FastUtilityModel;
            var aspect = NormalizeAspectRatio(spec.EffectiveAspectRatio);
            var request = new AnthropicRequest
            {
                Model = model,
                MaxTokens = SvgFallbackMaxTokens,
                System = "You are an expert vector illustrator. Create ONE self-contained SVG for the described asset. " +
                         "Rules: reply with ONLY the <svg> markup — no prose, no code fences; include a viewBox; " +
                         "no external references (fonts, images, scripts); keep it well under 200 KB.",
                Messages =
                [
                    new AnthropicMessage("user",
                        (aspect is not null ? $"Aspect ratio {aspect}. " : "") +
                        $"Asset description: {Truncate(prompt, MaxPromptChars)}")
                ],
            };

            // The requester's own runner only (their subscription, $0) — there is no
            // platform-key fallback. A miss fails this asset with the reason and what to do.
            var response = await runnerUtility.TryCompleteAsync(
                orgId, requesterUserId, request, timeout.Token);
            if (response is null)
                return (null, RunnerUtilityUnavailableException.MessageFor(
                    "let it draw this asset as an SVG"));

            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                OrgId: orgId,
                ProjectId: projectId,
                TaskId: null,
                AgentId: agentId,
                ExecutionId: null,
                Model: model,
                InputTokens: response.InputTokens,
                OutputTokens: response.OutputTokens,
                CacheReadTokens: response.CacheReadTokens,
                CacheWriteTokens: response.CacheWriteTokens,
                FlatCostBase: 0m,
                UserId: requesterUserId), ct);

            // Tolerate stray text around the markup — take the outermost <svg>…</svg>.
            var start = response.Text.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            var end = response.Text.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
            if (start < 0 || end < start)
                return (null, "The model did not return SVG markup.");

            var svg = response.Text[start..(end + "</svg>".Length)];
            var bytes = Encoding.UTF8.GetBytes(svg);
            if (bytes.Length > MaxSvgBytes)
                return (null, $"SVG exceeds the {MaxSvgBytes / 1024} KB limit.");

            return ((bytes, "image/svg+xml"), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var timedOut = timeout.IsCancellationRequested && !ct.IsCancellationRequested;
            logger.LogError(ex, "Runner SVG drawing {Outcome}", timedOut ? "timed out" : "failed");
            return (null, timedOut
                ? "Drawing the SVG exceeded its 3-minute budget."
                : "The runner's model did not respond.");
        }
    }

    /// <summary>
    /// Where a generated asset is filed.
    ///
    /// An iteration stays where the user filed the lineage — a new version must not jump back
    /// to the gallery root — and a NEW asset lands in the folder the user has open, so it does
    /// not have to be found and moved after every request. Neither is the model's decision:
    /// the open folder is a fact about where the user is standing.
    /// </summary>
    internal static string FolderFor(DesignAsset? parent, string? openFolderPath) =>
        parent?.FolderPath ?? openFolderPath ?? "";

    private async Task<DesignAsset> PersistAsync(
        Ulid orgId, Ulid projectId, Ulid agentId, Ulid conversationId, Ulid messageId,
        string requesterUserId, string name, byte[] data, string mimeType,
        DesignAsset? parent, string? defaultFolderPath, CancellationToken ct)
    {
        var assetId = Ulid.NewUlid();
        const string extension = "svg";
        var blobName = $"{orgId}/{projectId}/{assetId}.{extension}";

        using (var stream = new MemoryStream(data))
            await blobStorage.UploadAsync(BlobContainerName, blobName, stream, mimeType, ct);

        var version = 1;
        var rootAssetId = assetId;
        if (parent is not null)
        {
            rootAssetId = parent.RootAssetId;
            version = 1 + await db.DesignAssets.AsNoTracking()
                .Where(a => a.OrgId == orgId && a.RootAssetId == parent.RootAssetId)
                .MaxAsync(a => a.Version, ct);
        }

        var asset = new DesignAsset
        {
            Id = assetId,
            OrgId = orgId,
            ProjectId = projectId,
            AgentId = agentId,
            ConversationId = conversationId,
            MessageId = messageId,
            Name = name,
            FileName = $"{name}.{extension}",
            Kind = DesignAssetKind.Svg,
            MimeType = mimeType,
            BlobContainer = BlobContainerName,
            BlobName = blobName,
            SizeBytes = data.Length,
            Prompt = null,
            SourceModel = SvgSourceModel,
            ParentAssetId = parent?.Id,
            RootAssetId = rootAssetId,
            Version = version,
            FolderPath = FolderFor(parent, defaultFolderPath),
            RequestedByUserId = requesterUserId,
        };

        db.DesignAssets.Add(asset);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created design asset {AssetId} (svg, v{Version}) in project {ProjectId}",
            asset.Id, version, projectId);

        return asset;
    }

    /// <summary>Names become file names and blob paths — reduce to a safe slug.</summary>
    private static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if ((c == '-' || c == '_' || c == ' ') && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) return null;
        return slug.Length > 100 ? slug[..100] : slug;
    }

    private static string? NormalizeAspectRatio(string? aspectRatio)
    {
        var trimmed = aspectRatio?.Trim();
        return trimmed is not null && AllowedAspectRatios.Contains(trimmed) ? trimmed : null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
