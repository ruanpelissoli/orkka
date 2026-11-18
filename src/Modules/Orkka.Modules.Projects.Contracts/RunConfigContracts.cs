namespace Orkka.Modules.Projects.Contracts;

using System.Text.Json;

/// <summary>
/// What kind of thing a run target is. Drives nothing but presentation and the picker's
/// defaults — "run just the mobile app" is a real request, and the user cannot express it if
/// every target looks the same in the list.
///
/// Strings rather than an enum because every consumer is a boundary: model output, a JSON
/// column, and a JSON response the UI switches on. An unrecognised kind normalizes to
/// <see cref="Other"/> instead of failing a deserialization.
/// </summary>
public static class RunTargetKinds
{
    public const string Web = "web";
    public const string Api = "api";
    public const string Mobile = "mobile";
    public const string Desktop = "desktop";
    public const string Worker = "worker";
    public const string Database = "database";

    /// <summary>Supporting services the app needs but that are not the app: a Postgres or
    /// Redis started with `docker compose up <service>`. The runner probes Docker before
    /// starting one, and dependents wait for its port.</summary>
    public const string Infra = "infra";
    public const string Other = "other";

    public static readonly string[] All =
        [Web, Api, Mobile, Desktop, Worker, Database, Infra, Other];

    /// <summary>Targets a browser can open — what the "Live app" link points at.</summary>
    public static bool IsBrowsable(string? kind)
        => kind is Web or Mobile or Desktop;

    public static string Normalize(string? kind)
        => kind is not null && All.Contains(kind.Trim().ToLowerInvariant())
            ? kind.Trim().ToLowerInvariant()
            : Other;
}

/// <summary>
/// One process the Run button can start, as detected once from the repos and then stored.
///
/// Deliberately no environment-variable bag: a target says how to START the app, never what
/// secrets to give it. On the user's own machine their .env is already in the working copy;
/// storing a copy in Orkka's database would recreate exactly the leak the worktree setup
/// command was designed to avoid.
/// </summary>
/// <param name="Key">Stable slug, unique within the project ("web", "api", "mobile"). The
/// identity used by DependsOn and by the runner when it replaces a running process, so it
/// must survive re-detection of the same target.</param>
/// <param name="WorkingDirectory">Relative to the repo root — monorepos put the web app in
/// "apps/web". Null means the repo root.</param>
/// <param name="InstallCommand">What to run when dependencies are missing. The runner runs
/// it ahead of <paramref name="Command"/> only when they are evidently absent (a JS app with
/// no node_modules) — a fresh clone starts, a machine mid-work is not surprised by an
/// `npm ci`.</param>
/// <param name="Port">The port the target listens on, when it has one. This is what makes
/// "shut down whatever is already running" possible, what turns into a clickable URL, and
/// what dependents wait on before they start.</param>
/// <param name="ManualStep">A prerequisite Orkka cannot do for the user (an emulator, a
/// simulator, Docker running). Shown before they press Run rather than as a cryptic failure
/// afterwards.</param>
/// <param name="AutoSelect">Whether the picker ticks this target by default. A web app and
/// its API, yes; a mobile app that needs a simulator, no.</param>
/// <param name="StopCommand">Run before the process is killed. `docker compose up` targets
/// need `docker compose stop …`, or the containers outlive the Stop button.</param>
/// <param name="RestartOnMerge">Restart this target when a task merges. False for anything
/// that hot-reloads (a Next.js/Vite dev server, `dotnet watch`), true for an API started
/// without a watcher — the code changed, and only a restart shows it.</param>
/// <param name="PreviewPath">The path to open in the browser, appended to the URL. "/" when
/// null.</param>
public sealed record RunTargetDto(
    string Key,
    string Label,
    string Kind,
    string RepoId,
    string Command,
    string? WorkingDirectory = null,
    string? InstallCommand = null,
    int? Port = null,
    string? Url = null,
    string[]? DependsOn = null,
    string? ManualStep = null,
    bool AutoSelect = true,
    string? StopCommand = null,
    bool RestartOnMerge = false,
    string? PreviewPath = null);

/// <summary>How a project's run config came to be — an AI pass over the repos, the repo's
/// own <c>orkka.json</c> manifest, or the user correcting it. A manual edit is never
/// silently overwritten by re-detection.</summary>
public enum RunConfigSource
{
    Ai,
    Manual,
    Manifest,
}

/// <param name="Stale">The manifests changed since detection (a new package.json script, a
/// new service in the compose file), so the stored commands may be out of date. A hint for a
/// re-detect button — never an automatic re-run, which would spend tokens on every
/// dependency bump.</param>
public sealed record ProjectRunConfigDto(
    RunTargetDto[] Targets,
    string? Notes,
    RunConfigSource Source,
    DateTimeOffset DetectedAt,
    bool Stale);

/// <summary>
/// Works out how to run a project by reading its repos once, then remembers the answer.
///
/// "Once" is the point: asking a model how to start the app on every press would cost tokens
/// for an answer that changes only when the repo does.
/// </summary>
public interface IRunConfigDetector
{
    /// <summary>The stored config, or null when the project has never been detected. Cheap —
    /// no model call, though it does fingerprint the repos to fill in
    /// <see cref="ProjectRunConfigDto.Stale"/>.</summary>
    Task<ProjectRunConfigDto?> GetAsync(Ulid orgId, Ulid projectId, CancellationToken ct = default);

    /// <summary>The stored config as a plain row read — no fingerprinting, so no repo tree
    /// walk (over a runner link that is a dozen file reads). For pressing Run, which does
    /// not care about staleness; <see cref="ProjectRunConfigDto.Stale"/> is always false
    /// here. Null when never detected.</summary>
    Task<ProjectRunConfigDto?> GetStoredAsync(
        Ulid orgId, Ulid projectId, CancellationToken ct = default);

    /// <summary>Reads the project's repos, asks the model how to run them, and stores the
    /// result. Returns the stored config unchanged when it is still current and
    /// <paramref name="force"/> is false — this is the token-saving path the Run button
    /// takes.</summary>
    Task<ProjectRunConfigDto> DetectAsync(
        RunConfigDetectionRequest request, CancellationToken ct = default);

    /// <summary>Replaces the targets with the user's own. Marks the config
    /// <see cref="RunConfigSource.Manual"/> and re-fingerprints, so a correction is not
    /// immediately flagged stale.</summary>
    Task<ProjectRunConfigDto> SaveAsync(
        Ulid orgId, Ulid projectId, RunTargetDto[] targets, string? notes,
        CancellationToken ct = default);
}

/// <param name="RequesterUserId">Who to attribute the detection spend to — whoever pressed
/// the button owns its cost.</param>
public sealed record RunConfigDetectionRequest(
    Ulid OrgId,
    Ulid ProjectId,
    string? RequesterUserId = null,
    bool Force = false);

/// <summary>
/// Serialization for the stored targets blob. Centralized because three layers touch it —
/// the detector writes it, the endpoints read it, and the run hub turns it into processes —
/// and a casing mismatch between any two of them is a silently empty target list.
/// </summary>
public static class RunConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(RunTargetDto[] targets)
        => JsonSerializer.Serialize(targets, Options);

    /// <summary>Never throws: a config written by an older shape must degrade to "nothing
    /// detected yet" (which the UI offers to fix with one click), not to a 500 on the
    /// project page.</summary>
    public static RunTargetDto[] Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<RunTargetDto[]>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
