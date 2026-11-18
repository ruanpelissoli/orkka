namespace Orkka.Modules.Projects.Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.Modules.Usage.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Projects.Domain;
using Orkka.Modules.SourceControl.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>
/// Works out how to start a project's apps by reading its repos, and remembers the answer.
///
/// The economics are the design: a model call per Run press would be absurd for a question
/// whose answer changes only when the repo does. So detection happens once, the result is
/// stored, and a fingerprint over the manifests that informed it decides whether it is worth
/// asking again. A changed fingerprint marks the config stale and offers a re-detect — it
/// never triggers one, because a dependency bump is not a reason to spend the user's tokens.
/// </summary>
public sealed class RunConfigDetector(
    ProjectsDbContext db,
    // Per-repo: a folder on the requesting user's machine is read through their runner, a
    // forge repo through the platform mirror. Reading everything through the platform reader
    // made a hybrid project's Run button answer "no repositories Orkka can read" (2026-09-16).
    Features.ProjectRepos.RepoReaders readers,
    Features.ProjectRepos.RepoCloneSynchronizer repoClones,
    IUserRunnerUtility runnerUtility,
    IUsageRecorder usageRecorder,
    IOptions<AnthropicOptions> anthropicOptions,
    ILogger<RunConfigDetector> logger) : IRunConfigDetector
{
    private const int MaxTokens = 4096;

    /// <summary>Enough of the tree to spot a monorepo's packages and a mobile app's folder;
    /// far short of the point where paths crowd out the manifests that actually answer the
    /// question.</summary>
    private const int MaxTreePaths = 400;

    /// <summary>Per-manifest cap. A lockfile-sized package.json is rare but a Gradle build
    /// script can run long, and the scripts block is always near the top.</summary>
    private const int MaxManifestChars = 6000;

    private const int MaxManifestsPerRepo = 12;

    /// <summary>
    /// Files that answer "how do I run this". Ordered by how much they tell us, because the
    /// per-repo cap cuts from the end.
    ///
    /// Filenames rather than a directory sweep: a repo with 300 package.json files under
    /// node_modules would otherwise bury the one that matters.
    /// </summary>
    private static readonly string[] ManifestNames =
    [
        RunManifest.FileName,
        "package.json",
        "docker-compose.yml",
        "docker-compose.yaml",
        "compose.yaml",
        "Makefile",
        "justfile",
        "Procfile",
        "go.mod",
        "pubspec.yaml",
        "app.json",
        "Cargo.toml",
        "pyproject.toml",
        "requirements.txt",
        "manage.py",
        "Gemfile",
        "build.gradle",
        "build.gradle.kts",
        "settings.gradle.kts",
        "pnpm-workspace.yaml",
        "turbo.json",
        "nx.json",
        "angular.json",
        "vite.config.ts",
        "next.config.ts",
        "next.config.js",
        ".nvmrc",
        "README.md",
    ];

    /// <summary>Extensions worth reading when the filename alone isn't fixed — .NET projects
    /// and solutions are named after their assembly.</summary>
    private static readonly string[] ManifestExtensions = [".csproj", ".sln", ".slnx"];

    /// <summary>Vendor and build directories never explain how to start anything, and their
    /// manifests are the loudest noise in the tree.</summary>
    private static readonly string[] IgnoredSegments =
    [
        "node_modules/", "/obj/", "/bin/", ".git/", "vendor/", "dist/", "build/", ".next/",
        "target/", "Pods/", ".venv/", "__pycache__/",
    ];

    public async Task<ProjectRunConfigDto?> GetAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default)
    {
        var stored = await db.ProjectRunConfigs
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.OrgId == orgId, ct);

        // Best-effort: a repo that cannot be read right now (runner offline, never synced)
        // must not turn "show me the run config" into an error. An empty fingerprint means
        // "could not tell", which we report as not stale rather than nagging falsely.
        var evidence = await CollectEvidenceAsync(orgId, projectId, ct);

        // A project whose repos all carry orkka.json never needs a Detect press and is never
        // stale: reading the manifest is free, so a changed one is simply re-read here — the
        // panel opens on the commands the repo says are current. A manual edit is the one
        // thing that wins over the manifest, and only until the manifest changes again.
        if (evidence.AllHaveManifests
            && (stored is null || stored.Fingerprint != evidence.Fingerprint))
        {
            stored = await StoreFromManifestsAsync(stored, orgId, projectId, evidence, ct);
            return ToDto(stored, stale: false);
        }

        if (stored is null)
            return null;

        var stale = evidence.Fingerprint.Length > 0 && evidence.Fingerprint != stored.Fingerprint;
        return ToDto(stored, stale);
    }

    public async Task<ProjectRunConfigDto?> GetStoredAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default)
    {
        var stored = await db.ProjectRunConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.OrgId == orgId, ct);
        return stored is null ? null : ToDto(stored, stale: false);
    }

    public async Task<ProjectRunConfigDto> DetectAsync(
        RunConfigDetectionRequest request, CancellationToken ct = default)
    {
        var stored = await db.ProjectRunConfigs
            .FirstOrDefaultAsync(c => c.ProjectId == request.ProjectId && c.OrgId == request.OrgId, ct);

        // IGitRepoReader never clones on demand — on a host that hasn't touched these repos
        // yet (the hosted API pod, where tasks run elsewhere) every read would fail as "not
        // cloned yet" and detection would wrongly report the project as having no readable
        // repos. Syncing belongs to this explicit user action only: GetAsync's staleness
        // probe must stay a cheap read, not a fetch per panel open. Per-repo best-effort —
        // one unreachable repo must not block detecting the others.
        // Include(Project): the synchronizer's installation resolver branches on
        // Project.Type (managed vs customer installation) via the navigation property.
        var projectRepos = await db.ProjectRepos
            .Include(r => r.Project)
            .Where(r => r.ProjectId == request.ProjectId && r.OrgId == request.OrgId)
            .ToListAsync(ct);
        foreach (var repo in projectRepos)
        {
            var syncError = await repoClones.EnsureAsync(
                repo, "detect how to run this project", ct);
            if (syncError is not null)
                logger.LogWarning(
                    "Run-config detection could not sync repo {RepoName} ({RepoId}): {Error}",
                    repo.Name, repo.Id, syncError);
        }

        var evidence = await CollectEvidenceAsync(request.OrgId, request.ProjectId, ct);
        if (evidence.Repos.Count == 0)
            throw new RunConfigDetectionException(
                "This project has no repositories Orkka can read yet — link one first.");

        // The saving that makes the whole feature affordable: same repos, same answer.
        if (!request.Force && stored is not null && stored.Fingerprint == evidence.Fingerprint)
            return ToDto(stored, stale: false);

        // Every repo says how to run itself: no model, no tokens, no guessing. Force is
        // irrelevant here — there is nothing a second model pass could know that the
        // manifest does not.
        if (evidence.AllHaveManifests)
        {
            stored = await StoreFromManifestsAsync(stored, request.OrgId, request.ProjectId, evidence, ct);
            return ToDto(stored, stale: false);
        }

        // On the requester's own runner (their subscription, $0), recorded at $0
        // (FlatCostBase). No platform-key fallback: a miss is a RunConfigDetectionException
        // the user can act on — connect the runner, or type the commands in by hand.
        var detectionRequest = new AnthropicRequest
        {
            Model = anthropicOptions.Value.UtilityModel,
            MaxTokens = MaxTokens,
            System = RunConfigPrompts.DetectRunTargets,
            Messages = [new AnthropicMessage("user", evidence.Prompt)],
        };
        var response = await runnerUtility.TryCompleteAsync(
            request.OrgId, request.RequesterUserId, detectionRequest, ct)
            ?? throw new RunConfigDetectionException(RunnerUtilityUnavailableException.MessageFor(
                "detect how to run this project") + " Or add the commands yourself and Run will use them.");

        if (response.InputTokens > 0 || response.OutputTokens > 0)
        {
            await usageRecorder.RecordUsageAsync(new RecordUsageCommand(
                request.OrgId, request.ProjectId, null, null, null,
                anthropicOptions.Value.UtilityModel, response.InputTokens, response.OutputTokens,
                FlatCostBase: 0m,
                UserId: request.RequesterUserId), ct);
        }

        DetectedRunConfig parsed;
        try
        {
            parsed = ModelJson.Deserialize<DetectedRunConfig>(response.Text, "runConfig");
        }
        catch (ModelOutputException ex)
        {
            logger.LogError(
                "Run config detection failed for project {ProjectId}: {Reason}. Raw output: {RawOutput}",
                request.ProjectId, ex.Message, ex.RawOutput);
            throw;
        }

        // Mixed project: the manifest is authoritative for its own repo, whatever the model
        // said about it; the model fills in only the repos without one.
        var targets = Normalize(MergeManifests(parsed.Targets, evidence), evidence.RepoIds);
        if (targets.Length == 0)
            throw new RunConfigDetectionException(
                "Nothing runnable was found in this project's repositories. Add the commands " +
                "yourself and Run will use them.");

        if (stored is null)
        {
            stored = new ProjectRunConfig
            {
                Id = Ulid.NewUlid(),
                OrgId = request.OrgId,
                ProjectId = request.ProjectId,
                TargetsJson = RunConfigJson.Serialize(targets),
                Fingerprint = evidence.Fingerprint,
            };
            db.ProjectRunConfigs.Add(stored);
        }
        else
        {
            stored.TargetsJson = RunConfigJson.Serialize(targets);
            stored.Fingerprint = evidence.Fingerprint;
        }
        stored.Notes = string.IsNullOrWhiteSpace(parsed.Notes) ? null : parsed.Notes.Trim();
        stored.Source = RunConfigSource.Ai;
        stored.DetectedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Detected {Count} run target(s) for project {ProjectId}: {Targets}",
            targets.Length, request.ProjectId, string.Join(", ", targets.Select(t => t.Key)));

        return ToDto(stored, stale: false);
    }

    public async Task<ProjectRunConfigDto> SaveAsync(
        Ulid orgId, Ulid projectId, RunTargetDto[] targets, string? notes,
        CancellationToken ct = default)
    {
        var evidence = await CollectEvidenceAsync(orgId, projectId, ct);
        var normalized = Normalize(targets, evidence.RepoIds);

        var stored = await db.ProjectRunConfigs
            .FirstOrDefaultAsync(c => c.ProjectId == projectId && c.OrgId == orgId, ct);
        if (stored is null)
        {
            stored = new ProjectRunConfig
            {
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                TargetsJson = RunConfigJson.Serialize(normalized),
                Fingerprint = evidence.Fingerprint,
            };
            db.ProjectRunConfigs.Add(stored);
        }
        else
        {
            stored.TargetsJson = RunConfigJson.Serialize(normalized);
            // Re-fingerprinting on a manual save is the point: the user has just told us the
            // commands are right for the repos as they are now, so flagging their own edit as
            // stale would be nonsense.
            stored.Fingerprint = evidence.Fingerprint;
        }
        stored.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        stored.Source = RunConfigSource.Manual;
        stored.DetectedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return ToDto(stored, stale: false);
    }

    /// <summary>Upserts the stored config from the repos' own manifests. Free, so it is what
    /// both the panel-open read and an explicit Detect do when every repo has one.</summary>
    private async Task<ProjectRunConfig> StoreFromManifestsAsync(
        ProjectRunConfig? stored, Ulid orgId, Ulid projectId, Evidence evidence,
        CancellationToken ct)
    {
        var targets = Normalize(
            [.. evidence.Repos.SelectMany(r => r.Manifest!.Targets)], evidence.RepoIds);
        var notes = string.Join("\n\n", evidence.Repos
            .Select(r => r.Manifest!.Notes)
            .Where(n => !string.IsNullOrWhiteSpace(n)));

        if (stored is null)
        {
            stored = new ProjectRunConfig
            {
                Id = Ulid.NewUlid(),
                OrgId = orgId,
                ProjectId = projectId,
                TargetsJson = RunConfigJson.Serialize(targets),
                Fingerprint = evidence.Fingerprint,
            };
            db.ProjectRunConfigs.Add(stored);
        }
        else
        {
            stored.TargetsJson = RunConfigJson.Serialize(targets);
            stored.Fingerprint = evidence.Fingerprint;
        }
        stored.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        stored.Source = RunConfigSource.Manifest;
        stored.DetectedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Run config for project {ProjectId} read from {File}: {Targets}",
            projectId, RunManifest.FileName, string.Join(", ", targets.Select(t => t.Key)));
        return stored;
    }

    /// <summary>Model targets for repos WITHOUT a manifest, plus every manifest target
    /// verbatim. The model was told to copy manifests; this makes sure it could not have
    /// changed them.</summary>
    internal static RunTargetDto[] MergeManifests(RunTargetDto[]? modelTargets, Evidence evidence)
    {
        var manifestRepos = evidence.Repos
            .Where(r => r.Manifest is not null)
            .Select(r => r.RepoId.ToString())
            .ToHashSet(StringComparer.Ordinal);
        if (manifestRepos.Count == 0)
            return modelTargets ?? [];

        var fromManifests = evidence.Repos
            .Where(r => r.Manifest is not null)
            .SelectMany(r => r.Manifest!.Targets);
        var fromModel = (modelTargets ?? [])
            .Where(t => !manifestRepos.Contains(t.RepoId));
        return [.. fromManifests, .. fromModel];
    }

    private static ProjectRunConfigDto ToDto(ProjectRunConfig stored, bool stale)
        => new(RunConfigJson.Deserialize(stored.TargetsJson), stored.Notes, stored.Source,
            stored.DetectedAt, stale);

    /// <summary>
    /// Keeps the stored config honest whatever produced it: model output and hand-written
    /// JSON both arrive here. Targets pointing at repos that are not in this project, or with
    /// no command to run, are dropped rather than stored to fail later at the PTY.
    /// </summary>
    internal static RunTargetDto[] Normalize(
        RunTargetDto[]? targets, IReadOnlyList<string> repoIds)
    {
        if (targets is null)
            return [];

        var validRepoIds = repoIds.ToHashSet(StringComparer.Ordinal);
        var defaultRepoId = repoIds.Count == 1 ? repoIds[0] : null;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RunTargetDto>();

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Command))
                continue;

            // A single-repo project is the common case and the one where a model is most
            // likely to echo a repo NAME instead of its id — recoverable, so recover it.
            var repoId = validRepoIds.Contains(target.RepoId) ? target.RepoId : defaultRepoId;
            if (repoId is null)
                continue;

            var key = Slug(target.Key, fallback: target.Label);
            if (key.Length == 0 || !seenKeys.Add(key))
                continue;

            // Both the port and the URL come from the VALIDATED port. Deriving the URL from
            // the raw one instead would hand the UI a link to localhost:0 for a port we had
            // just decided not to trust.
            var port = target.Port is > 0 and < 65536 ? target.Port : null;
            var kind = RunTargetKinds.Normalize(target.Kind);
            // A Postgres container has a port and no URL a browser could open — a link to
            // http://localhost:5432 would be a link to a hang.
            var opensInBrowser = kind is not (RunTargetKinds.Infra or RunTargetKinds.Database
                or RunTargetKinds.Worker);

            result.Add(target with
            {
                Key = key,
                RepoId = repoId,
                Kind = kind,
                Label = string.IsNullOrWhiteSpace(target.Label) ? key : target.Label.Trim(),
                Command = target.Command.Trim(),
                WorkingDirectory = Blank(target.WorkingDirectory),
                InstallCommand = Blank(target.InstallCommand),
                StopCommand = Blank(target.StopCommand),
                ManualStep = Blank(target.ManualStep),
                Port = port,
                Url = !opensInBrowser ? null
                    : port is { } p ? $"http://localhost:{p}" : Blank(target.Url),
                PreviewPath = opensInBrowser ? Blank(target.PreviewPath) : null,
                DependsOn = target.DependsOn ?? [],
            });
        }

        // Drop dependencies on targets that did not survive, so the runner's ordering never
        // waits on something that will never start.
        var keys = result.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. result.Select(t => t with
        {
            DependsOn = [.. t.DependsOn!.Where(d =>
                keys.Contains(d) && !string.Equals(d, t.Key, StringComparison.OrdinalIgnoreCase))],
        })];
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Slug(string? value, string? fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (string.IsNullOrWhiteSpace(source))
            return "";
        var sb = new StringBuilder(source.Length);
        foreach (var c in source.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    // ── Evidence gathering ───────────────────────────────────────────────────

    /// <param name="Manifest">The repo's own <c>orkka.json</c> run section, when it has a
    /// usable one.</param>
    internal sealed record RepoEvidence(
        Ulid RepoId, string Name, string Block, string Fingerprint, ParsedRunManifest? Manifest);

    internal sealed record Evidence(
        IReadOnlyList<RepoEvidence> Repos, string Prompt, string Fingerprint)
    {
        /// <summary>The repos a target may legitimately point at.</summary>
        public IReadOnlyList<string> RepoIds { get; } =
            [.. Repos.Select(r => r.RepoId.ToString())];

        /// <summary>Every readable repo carries a manifest — detection needs no model.</summary>
        public bool AllHaveManifests => Repos.Count > 0 && Repos.All(r => r.Manifest is not null);
    }

    private async Task<Evidence> CollectEvidenceAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct)
    {
        var repos = await db.ProjectRepos
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.OrgId == orgId)
            .OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.DefaultBranch })
            .ToListAsync(ct);

        var collected = new List<RepoEvidence>();
        foreach (var repo in repos)
        {
            var evidence = await ReadRepoAsync(orgId, repo.Id, repo.Name, repo.DefaultBranch, ct);
            if (evidence is not null)
                collected.Add(evidence);
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("# Project repositories").AppendLine();
        foreach (var repo in collected)
            prompt.AppendLine(repo.Block);

        // Empty when nothing could be read — the caller treats that as "cannot tell",
        // never as "nothing changed".
        var fingerprint = collected.Count == 0
            ? ""
            : Sha256(string.Join("\n", collected.Select(r => $"{r.RepoId}:{r.Fingerprint}")));

        return new Evidence(collected, prompt.ToString(), fingerprint);
    }

    private async Task<RepoEvidence?> ReadRepoAsync(
        Ulid orgId, Ulid repoId, string name, string branch, CancellationToken ct)
    {
        var reader = readers.Current;
        var tree = await reader.GetFileTreeAsync(orgId, repoId, branch, ct);
        if (tree.IsFailure || tree.Value is null)
        {
            logger.LogInformation(
                "Run config: repo {RepoId} could not be read ({Error}) — skipped", repoId, tree.Error);
            return null;
        }

        var paths = tree.Value
            .Where(node => !IgnoredSegments.Any(segment =>
                ("/" + node.Path).Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .Select(node => node.Path)
            .ToList();

        var manifestPaths = paths
            .Where(IsManifest)
            // Shallowest first: the root package.json describes the project, one six levels
            // down describes a fixture.
            .OrderBy(p => p.Count(c => c is '/'))
            .ThenBy(p => p.Length)
            .Take(MaxManifestsPerRepo)
            .ToList();

        var block = new StringBuilder();
        var fingerprint = new StringBuilder();
        block.AppendLine($"## Repository: {name}");
        block.AppendLine($"- repoId: `{repoId}`");
        block.AppendLine($"- default branch: `{branch}`").AppendLine();

        block.AppendLine("### File tree (truncated)").AppendLine("```");
        foreach (var path in paths.Take(MaxTreePaths))
            block.AppendLine(path);
        block.AppendLine("```").AppendLine();

        ParsedRunManifest? manifest = null;
        foreach (var path in manifestPaths)
        {
            var content = await reader.GetFileContentAsync(orgId, repoId, branch, path, ct);
            if (content.IsFailure || string.IsNullOrWhiteSpace(content.Value))
                continue;
            // Only the ROOT orkka.json is the repo's contract; one in a subfolder is a
            // fixture, a template, or somebody else's project vendored in.
            if (string.Equals(path, RunManifest.FileName, StringComparison.OrdinalIgnoreCase))
                manifest = RunManifest.TryParse(content.Value, repoId.ToString());
            var text = content.Value.Length > MaxManifestChars
                ? content.Value[..MaxManifestChars] + "\n… (truncated)"
                : content.Value;
            block.AppendLine($"### {path}").AppendLine("```");
            block.AppendLine(text);
            block.AppendLine("```").AppendLine();
            fingerprint.Append(path).Append(':').Append(Sha256(text)).Append('\n');
        }

        // Manifest CONTENT, not the file list: adding a test file must not invalidate a
        // perfectly good run config, while adding a `dev` script should.
        return new RepoEvidence(
            repoId, name, block.ToString(), Sha256(fingerprint.ToString()), manifest);
    }

    private static bool IsManifest(string path)
    {
        var fileName = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return ManifestNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)
            || ManifestExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>What the model is asked to return. Deliberately the wire shape minus the
    /// fields only Orkka can fill (nothing today) — one mapping is one place to get it
    /// wrong.</summary>
    private sealed record DetectedRunConfig(RunTargetDto[]? Targets, string? Notes);
}

/// <summary>Detection failed for a reason the user can act on — no repos, nothing runnable.
/// Distinct from a model/parse failure, which is ours to fix, not theirs.</summary>
public sealed class RunConfigDetectionException(string message) : InvalidOperationException(message);
