namespace Orkka.Modules.Trackers.Features.ProjectConfig;

using System.Text.Json;
using System.Text.RegularExpressions;
using Orkka.LocalRunner.Contracts;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Features.Connections;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.SharedKernel.Security;

/// <summary>The tracker status each pipeline step moves a ticket to; every one optional.</summary>
public sealed record TrackerStatusMapDto(
    string? PickedUp = null,
    string? Planning = null,
    string? Developing = null,
    string? InReview = null,
    string? PrOpened = null,
    string? Merged = null)
{
    public static TrackerStatusMapDto From(TrackerStatusMap map)
        => new(map.PickedUp, map.Planning, map.Developing, map.InReview, map.PrOpened, map.Merged);
}

/// <param name="RepoId">Where its tickets are worked; optional — an epic without one only scopes
/// intake (in Epics mode) and leaves routing to the rest.</param>
public sealed record TrackerEpicMappingDto(string EpicKey, string? EpicTitle, Ulid? RepoId, string? Subpath);

/// <param name="Kind">"component" (Jira), "label" (Jira labels / ADO tags) or "areaPath" (ADO;
/// also matches the paths under it).</param>
public sealed record TrackerRoutingRuleDto(string Kind, string Value, Ulid RepoId, string? Subpath);

public sealed record ProjectTrackerConfigResponse(
    Ulid Id,
    Ulid ProjectId,
    string OwnerUserId,
    Ulid TrackerConnectionId,
    // Null when the connection row is gone (it never is, today — kept for the UI's sake).
    TrackerConnectionResponse? Connection,
    string Scope,
    string? ExtraClause,
    bool IncludeDescendants,
    bool AutoSubmit,
    int PollIntervalSeconds,
    TrackerStatusMapDto StatusMap,
    string BranchTemplate,
    bool Enabled,
    DateTimeOffset? LastPolledAt,
    string? LastPollError,
    TrackerEpicMappingDto[] Epics,
    // "All" (every ticket assigned to the owner in an intake status) or "Epics".
    string ScopeMode,
    string[] IntakeStatuses,
    TrackerRoutingRuleDto[] RoutingRules,
    Ulid? DefaultRepoId,
    int StoryMode,
    // The board intake is limited to (Jira board id / Azure DevOps team id / GitHub project node
    // id); null = every board.
    string? BoardId = null,
    string? BoardName = null,
    // GitHub: the single-select field holding the status (null = "Status").
    string? StatusField = null,
    // GitHub: intake only from these repositories (owner/name); empty = every repository.
    string[]? RepoFilter = null);

public sealed record SaveProjectTrackerRequest(
    Ulid TrackerConnectionId,
    string Scope,
    TrackerEpicMappingDto[]? Epics = null,
    string? ExtraClause = null,
    bool IncludeDescendants = false,
    bool AutoSubmit = true,
    int PollIntervalSeconds = ProjectTrackerConfig.DefaultPollIntervalSeconds,
    TrackerStatusMapDto? StatusMap = null,
    string? BranchTemplate = null,
    bool Enabled = true,
    string? ScopeMode = null,
    string[]? IntakeStatuses = null,
    TrackerRoutingRuleDto[]? RoutingRules = null,
    Ulid? DefaultRepoId = null,
    int StoryMode = 1,
    // Null or empty = every board in the scope. GitHub: the project — required.
    string? BoardId = null,
    string? BoardName = null,
    // GitHub: the single-select field holding the status; null = "Status".
    string? StatusField = null,
    // GitHub: owner/name repositories intake is limited to; null or empty = every repository.
    string[]? RepoFilter = null);

public sealed class SaveProjectTrackerValidator : Validator<SaveProjectTrackerRequest>
{
    public SaveProjectTrackerValidator()
    {
        // WithMessage covers only the rule right before it — the empty case needs its own, or an
        // empty scope came back as FluentValidation's "'scope' must not be empty."
        RuleFor(x => x.Scope)
            .NotEmpty().WithMessage("Pick the Jira project, Azure DevOps project or GitHub owner the tickets live in.")
            .MaximumLength(256).WithMessage("Pick the Jira project, Azure DevOps project or GitHub owner the tickets live in.");
        RuleFor(x => x.ExtraClause).MaximumLength(2000);
        RuleFor(x => x.BoardId).MaximumLength(128).WithMessage("Pick a board from the list.");
        RuleFor(x => x.BoardName).MaximumLength(256).WithMessage("Pick a board from the list.");
        RuleFor(x => x.StatusField).MaximumLength(200).WithMessage("Pick the status field from the list.");
        RuleFor(x => x.RepoFilter)
            .Must(r => r is null || (r.Length <= 100 && r.All(ProjectTrackerMappers.IsRepoName)))
            .WithMessage("The repository filter lists repositories as owner/name, at most 100 of them.");
        RuleFor(x => x.PollIntervalSeconds)
            .InclusiveBetween(ProjectTrackerConfig.MinPollIntervalSeconds, ProjectTrackerConfig.MaxPollIntervalSeconds)
            .WithMessage($"Check for tickets every {ProjectTrackerConfig.MinPollIntervalSeconds}–{ProjectTrackerConfig.MaxPollIntervalSeconds} seconds.");
        RuleFor(x => x.BranchTemplate)
            .Must(t => t is null || (t.Contains("{key}", StringComparison.Ordinal) && t.Length <= 200))
            .WithMessage("The branch template must contain {key} — that is how the tracker links the branch to its ticket.");
        RuleFor(x => x.ScopeMode)
            .Must(m => m is null || ProjectTrackerMappers.TryParseScopeMode(m, out _))
            .WithMessage("The scope is either all your tickets (All) or only some epics (Epics).");
        RuleFor(x => x.Epics).NotEmpty()
            .When(x => ProjectTrackerMappers.TryParseScopeMode(x.ScopeMode, out var mode) && mode == TrackerScopeMode.Epics)
            .WithMessage("Add at least one epic, or pick up all your tickets.");
        RuleFor(x => x.Epics)
            .Must(e => e!.Select(m => m.EpicKey.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == e!.Length)
            .When(x => x.Epics is { Length: > 0 })
            .WithMessage("Each epic can be mapped once.");
        RuleFor(x => x.Epics!.Length).LessThanOrEqualTo(200).When(x => x.Epics is not null);
        RuleFor(x => x.IntakeStatuses)
            .Must(s => s is null || (s.Length <= 50 && s.All(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length <= 200)))
            .WithMessage("Intake statuses are status names, at most 50 of them.");
        RuleFor(x => x.RoutingRules!.Length).LessThanOrEqualTo(200).When(x => x.RoutingRules is not null);
        RuleForEach(x => x.RoutingRules).ChildRules(rule =>
        {
            rule.RuleFor(r => r.Kind)
                .Must(k => ProjectTrackerMappers.TryParseRuleKind(k, out var kind) && kind != TrackerRuleKind.Epic)
                .WithMessage("A routing rule matches a component, a label or an area path (map epics in the epic list).");
            rule.RuleFor(r => r.Value).NotEmpty().MaximumLength(256);
            rule.RuleFor(r => r.Subpath)
                .Must(s => TrackerSubpath.TryNormalize(s, out _))
                .WithMessage("A subfolder is a relative path inside the repository, like packages/api — no '..', no drive letters.");
        });
        RuleForEach(x => x.Epics).ChildRules(epic =>
        {
            epic.RuleFor(e => e.EpicKey).NotEmpty().MaximumLength(64);
            epic.RuleFor(e => e.EpicTitle).MaximumLength(500);
            epic.RuleFor(e => e.Subpath)
                .Must(s => TrackerSubpath.TryNormalize(s, out _))
                .WithMessage("A subfolder is a relative path inside the repository, like packages/api — no '..', no drive letters.");
        });
        RuleFor(x => x.StatusMap!.PickedUp).MaximumLength(200).When(x => x.StatusMap is not null);
        RuleFor(x => x.StatusMap!.Planning).MaximumLength(200).When(x => x.StatusMap is not null);
        RuleFor(x => x.StatusMap!.Developing).MaximumLength(200).When(x => x.StatusMap is not null);
        RuleFor(x => x.StatusMap!.InReview).MaximumLength(200).When(x => x.StatusMap is not null);
        RuleFor(x => x.StatusMap!.PrOpened).MaximumLength(200).When(x => x.StatusMap is not null);
        RuleFor(x => x.StatusMap!.Merged).MaximumLength(200).When(x => x.StatusMap is not null);
    }
}

/// <summary>
/// A folder inside a repository, as the epic mapping stores it: forward slashes, no leading or
/// trailing slash, no <c>.</c>/<c>..</c> segments, no drive or root. It ends up in an agent's
/// prompt and a working directory on the developer's machine, so anything that could step
/// outside the repository is refused rather than cleaned up.
/// </summary>
internal static class TrackerSubpath
{
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var path = value.Trim().Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.StartsWith('~'))
            return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return true;
        if (segments.Any(s => s is "." or ".." || s.Trim().Length != s.Length))
            return false;

        normalized = string.Join('/', segments);
        return normalized.Length <= 500;
    }
}

internal static partial class ProjectTrackerMappers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A GitHub repository as <c>owner/name</c>.</summary>
    public static bool IsRepoName(string? value) => value is not null && RepoName().IsMatch(value.Trim());

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoName();

    /// <summary>The stored repository filter; unreadable = none.</summary>
    public static string[] ParseRepoFilter(string? json)
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<string[]>(json, Json);
            return [.. (parsed ?? []).Where(IsRepoName).Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeRepoFilter(string[]? repos)
        => JsonSerializer.Serialize((repos ?? []).Where(IsRepoName).Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), Json);

    /// <summary>The stored map, read tolerantly: keys in any case, unknown keys ignored, and the
    /// old <c>started</c> key (from before the steps were widened) read as <c>planning</c>.</summary>
    public static TrackerStatusMap ParseStatusMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new TrackerStatusMap();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new TrackerStatusMap();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && Blank(property.Value.GetString()) is { } value)
                    values[property.Name] = value;
            }
            string? Get(string key) => values.TryGetValue(key, out var v) ? v : null;
            return new TrackerStatusMap(
                Get("pickedUp"), Get("planning") ?? Get("started"), Get("developing"),
                Get("inReview"), Get("prOpened"), Get("merged"));
        }
        catch (JsonException)
        {
            return new TrackerStatusMap();
        }
    }

    public static string SerializeStatusMap(TrackerStatusMapDto? map)
        => JsonSerializer.Serialize(new TrackerStatusMap(
            Blank(map?.PickedUp), Blank(map?.Planning), Blank(map?.Developing),
            Blank(map?.InReview), Blank(map?.PrOpened), Blank(map?.Merged)), Json);

    public static ProjectTrackerConfigResponse ToResponse(this ProjectTrackerConfig c, TrackerConnection? connection)
    {
        var map = ParseStatusMap(c.StatusMapJson);
        return new ProjectTrackerConfigResponse(
            c.Id, c.ProjectId, c.OwnerUserId, c.TrackerConnectionId, connection?.ToResponse(),
            c.Scope, c.ExtraClause, c.IncludeDescendants, c.AutoSubmit, c.PollIntervalSeconds,
            TrackerStatusMapDto.From(map),
            c.BranchTemplate, c.Enabled, c.LastPolledAt, c.LastPollError,
            [.. c.Epics.Where(e => e.Kind == TrackerRuleKind.Epic).OrderBy(e => e.Position).ThenBy(e => e.EpicKey)
                .Select(e => new TrackerEpicMappingDto(e.EpicKey, e.EpicTitle, e.RepoId, e.Subpath))],
            c.ScopeMode.ToString(),
            ParseIntakeStatuses(c.IntakeStatusesJson),
            [.. c.Epics.Where(e => e.Kind != TrackerRuleKind.Epic && e.RepoId is not null).OrderBy(e => e.Position)
                .Select(e => new TrackerRoutingRuleDto(RuleKindName(e.Kind), e.EpicKey, e.RepoId!.Value, e.Subpath))],
            c.DefaultRepoId,
            c.StoryMode,
            c.BoardId,
            c.BoardId is null ? null : c.BoardName,
            c.StatusField,
            ParseRepoFilter(c.RepoFilterJson));
    }

    /// <summary>The stored intake statuses; an empty or unreadable list means "To Do".</summary>
    public static string[] ParseIntakeStatuses(string? json)
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<string[]>(json, Json);
            var cleaned = (parsed ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return cleaned.Length > 0 ? cleaned : ["To Do"];
        }
        catch (JsonException)
        {
            return ["To Do"];
        }
    }

    public static string SerializeIntakeStatuses(string[]? statuses)
    {
        var cleaned = (statuses ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return JsonSerializer.Serialize(cleaned.Length > 0 ? cleaned : ["To Do"], Json);
    }

    public static bool TryParseScopeMode(string? value, out TrackerScopeMode mode)
    {
        mode = TrackerScopeMode.All;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        return Enum.TryParse(value.Trim(), ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }

    public static bool TryParseRuleKind(string? value, out TrackerRuleKind kind)
    {
        kind = TrackerRuleKind.Epic;
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value.Trim(), ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    /// <summary>camelCase, as the UI sends it back: component, label, areaPath.</summary>
    public static string RuleKindName(TrackerRuleKind kind)
        => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The project's ticket source, or 204 when it has none.</summary>
public sealed class GetProjectTrackerEndpoint(
    TrackersDbContext db, IProjectResolver projectResolver)
    : EndpointWithoutRequest<ProjectTrackerConfigResponse>
{
    public override void Configure() => Get("/api/projects/{projectId}/tracker");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var config = await db.ProjectConfigs.Include(c => c.Epics)
            .FirstOrDefaultAsync(c => c.ProjectId == projectId, ct);
        if (config is null)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == config.TrackerConnectionId, ct);
        await Send.OkAsync(config.ToResponse(connection), ct);
    }
}

/// <summary>
/// Sets the project's ticket source. The caller becomes its owner and must hold the connection
/// on one of their own machines: their runner is what will poll the tracker with their token,
/// so the tickets picked up are the ones assigned to them.
/// </summary>
public sealed class SaveProjectTrackerEndpoint(
    TrackersDbContext db,
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IAgentAuditLog audit)
    : Endpoint<SaveProjectTrackerRequest, ProjectTrackerConfigResponse>
{
    public override void Configure() => Put("/api/projects/{projectId}/tracker");

    public override async Task HandleAsync(SaveProjectTrackerRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsManage))
            return;

        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var connection = await db.Connections.FirstOrDefaultAsync(
            c => c.Id == req.TrackerConnectionId && c.UserId == currentUser.UserId, ct);
        if (connection is null)
        {
            ThrowError(r => r.TrackerConnectionId,
                "Pick one of your own tracker connections — add one in Settings → Trackers (or `./orkka tracker add`).");
            return;
        }

        var repoIds = (await projectsApi.ListReposAsync(projectId, ct)).Select(r => r.Id).ToHashSet();
        var epics = req.Epics ?? [];
        var routingRules = req.RoutingRules ?? [];
        if (epics.FirstOrDefault(e => e.RepoId is { } repo && !repoIds.Contains(repo)) is { } stray)
        {
            ThrowError(r => r.Epics, $"Epic {stray.EpicKey} points at a repository that is not in this project.");
            return;
        }
        if (routingRules.FirstOrDefault(r => !repoIds.Contains(r.RepoId)) is { } strayRule)
        {
            ThrowError(r => r.RoutingRules, $"The rule for {strayRule.Value} points at a repository that is not in this project.");
            return;
        }
        if (req.DefaultRepoId is { } defaultRepo && !repoIds.Contains(defaultRepo))
        {
            ThrowError(r => r.DefaultRepoId, "The default repository is not in this project.");
            return;
        }
        ProjectTrackerMappers.TryParseScopeMode(req.ScopeMode, out var scopeMode);
        var github = connection.Kind == TrackerKinds.GitHub;
        if (github)
        {
            // The project IS the source: no whole-owner intake, no epics, no JQL/WIQL.
            if (string.IsNullOrWhiteSpace(req.BoardId))
            {
                ThrowError(r => r.BoardId, "Pick the GitHub project the issues are on.");
                return;
            }
            if (scopeMode == TrackerScopeMode.Epics || epics.Length > 0)
            {
                ThrowError(r => r.ScopeMode,
                    "GitHub Projects have no epics — an issue with sub-issues is worked as a Story. Pick up all your issues on the project.");
                return;
            }
            if (routingRules.FirstOrDefault(r => !string.Equals(r.Kind.Trim(), "label", StringComparison.OrdinalIgnoreCase)) is { } strayKind)
            {
                ThrowError(r => r.RoutingRules, $"GitHub issues are routed by label (or by their repository) — not by {strayKind.Kind}.");
                return;
            }
        }

        var config = await db.ProjectConfigs.Include(c => c.Epics)
            .FirstOrDefaultAsync(c => c.ProjectId == projectId, ct);
        var created = config is null;
        if (config is null)
        {
            config = new ProjectTrackerConfig
            {
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                ProjectId = projectId,
                OwnerUserId = currentUser.UserId,
                Scope = req.Scope.Trim(),
            };
            db.ProjectConfigs.Add(config);
        }

        config.OwnerUserId = currentUser.UserId;
        config.TrackerConnectionId = connection.Id;
        config.Scope = req.Scope.Trim();
        config.ExtraClause = github || string.IsNullOrWhiteSpace(req.ExtraClause) ? null : req.ExtraClause.Trim();
        config.BoardId = string.IsNullOrWhiteSpace(req.BoardId) ? null : req.BoardId.Trim();
        config.BoardName = config.BoardId is null
            ? null
            : string.IsNullOrWhiteSpace(req.BoardName) ? config.BoardId : req.BoardName.Trim();
        config.IncludeDescendants = !github && req.IncludeDescendants;
        config.StatusField = github && !string.IsNullOrWhiteSpace(req.StatusField) ? req.StatusField.Trim() : null;
        config.RepoFilterJson = ProjectTrackerMappers.SerializeRepoFilter(github ? req.RepoFilter : null);
        config.AutoSubmit = req.AutoSubmit;
        config.PollIntervalSeconds = req.PollIntervalSeconds;
        config.StatusMapJson = ProjectTrackerMappers.SerializeStatusMap(req.StatusMap);
        config.BranchTemplate = string.IsNullOrWhiteSpace(req.BranchTemplate)
            ? ProjectTrackerConfig.DefaultBranchTemplate
            : req.BranchTemplate.Trim();
        config.Enabled = req.Enabled;
        config.ScopeMode = scopeMode;
        config.IntakeStatusesJson = ProjectTrackerMappers.SerializeIntakeStatuses(req.IntakeStatuses);
        config.DefaultRepoId = req.DefaultRepoId;
        config.StoryMode = req.StoryMode;
        // A new configuration is a new question for the tracker; the last answer was to a
        // different one.
        config.LastPollError = null;

        db.EpicMappings.RemoveRange(config.Epics);
        config.Epics =
        [
            .. epics.Select((e, i) =>
            {
                TrackerSubpath.TryNormalize(e.Subpath, out var subpath);
                return new TrackerEpicMapping
                {
                    Id = Ulid.NewUlid(),
                    OrgId = currentUser.OrgId,
                    ConfigId = config.Id,
                    Kind = TrackerRuleKind.Epic,
                    EpicKey = e.EpicKey.Trim(),
                    EpicTitle = string.IsNullOrWhiteSpace(e.EpicTitle) ? null : e.EpicTitle.Trim(),
                    RepoId = e.RepoId,
                    Subpath = e.RepoId is null ? null : subpath,
                    Position = i,
                };
            }),
            .. routingRules
                // The same value twice under one kind would break the unique index; the first wins.
                .DistinctBy(r => (r.Kind.Trim().ToLowerInvariant(), r.Value.Trim().ToLowerInvariant()))
                .Select((r, i) =>
                {
                    TrackerSubpath.TryNormalize(r.Subpath, out var subpath);
                    ProjectTrackerMappers.TryParseRuleKind(r.Kind, out var kind);
                    return new TrackerEpicMapping
                    {
                        Id = Ulid.NewUlid(),
                        OrgId = currentUser.OrgId,
                        ConfigId = config.Id,
                        Kind = kind,
                        EpicKey = r.Value.Trim(),
                        RepoId = r.RepoId,
                        Subpath = subpath,
                        Position = i,
                    };
                }),
        ];

        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, AgentAuditEventType.TrackerConfigChanged,
            $"Ticket source {(created ? "set" : "changed")}: {connection.Kind} {connection.SiteUrl} / {config.Scope}"
                + (config.BoardId is null ? ", " : $" (board {config.BoardName}), ")
                + $"{config.ScopeMode}, {epics.Length} epic(s), {routingRules.Length} routing rule(s)",
            projectId: projectId,
            metadata: new
            {
                connection = connection.ConnectionKey,
                connection.Kind,
                config.Scope,
                board = config.BoardId is null ? null : new { id = config.BoardId, name = config.BoardName },
                config.StatusField,
                repoFilter = ProjectTrackerMappers.ParseRepoFilter(config.RepoFilterJson),
                scopeMode = config.ScopeMode.ToString(),
                epics = epics.Select(e => e.EpicKey).ToArray(),
                intakeStatuses = ProjectTrackerMappers.ParseIntakeStatuses(config.IntakeStatusesJson),
                config.AutoSubmit,
                config.Enabled,
            }), ct);

        await Send.OkAsync(config.ToResponse(connection), ct);
    }
}

public sealed class DeleteProjectTrackerEndpoint(
    TrackersDbContext db,
    IProjectResolver projectResolver,
    ICurrentUser currentUser,
    IAgentAuditLog audit)
    : EndpointWithoutRequest
{
    public override void Configure() => Delete("/api/projects/{projectId}/tracker");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.ProjectsManage))
            return;

        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var config = await db.ProjectConfigs.FirstOrDefaultAsync(c => c.ProjectId == projectId, ct);
        if (config is not null)
        {
            db.ProjectConfigs.Remove(config);
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(AgentAuditRecord.ByUser(
                currentUser.OrgId, currentUser.UserId, AgentAuditEventType.TrackerConfigChanged,
                "Ticket source removed", projectId: projectId), ct);
        }

        await Send.NoContentAsync(ct);
    }
}
