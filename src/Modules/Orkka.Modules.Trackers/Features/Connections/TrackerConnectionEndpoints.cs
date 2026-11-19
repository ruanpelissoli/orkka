namespace Orkka.Modules.Trackers.Features.Connections;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Trackers.Domain;
using Orkka.Modules.Trackers.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed record TrackerConnectionResponse(
    Ulid Id,
    string Key,
    string Kind,
    string SiteUrl,
    string? AccountId,
    string? DisplayName,
    bool Healthy,
    string? HealthDetail,
    string MachineName,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? CheckedAt,
    // GitHub: "gh" (the developer's gh login) or "token"; null for Jira / Azure DevOps.
    string? AuthMode = null);

internal static class TrackerConnectionMappers
{
    public static TrackerConnectionResponse ToResponse(this TrackerConnection c) => new(
        c.Id, c.ConnectionKey, c.Kind, c.SiteUrl, c.AccountId, c.DisplayName,
        c.Healthy, c.HealthDetail, c.MachineName, c.LastSeenAt, c.CheckedAt, c.AuthMode);
}

/// <summary>
/// The caller's own tracker connections, across their machines. Own only: a connection is a
/// token on one person's machine, and a project can only be fed by its owner's runner — a
/// colleague's connection is nothing the caller could use.
/// </summary>
public sealed class ListMyTrackerConnectionsEndpoint(TrackersDbContext db, ICurrentUser currentUser)
    : EndpointWithoutRequest<TrackerConnectionResponse[]>
{
    public override void Configure() => Get("/api/trackers/connections");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var rows = await db.Connections
            .Where(c => c.UserId == currentUser.UserId)
            .OrderBy(c => c.Kind).ThenBy(c => c.SiteUrl)
            .ToListAsync(ct);

        await Send.OkAsync([.. rows.Select(r => r.ToResponse())], ct);
    }
}

/// <param name="Kind">A <see cref="TrackerQueryKinds"/> value.</param>
public sealed record TrackerQueryBody(
    string Kind,
    string? Scope = null,
    string? Search = null,
    string[]? EpicKeys = null,
    string? ExtraClause = null,
    bool IncludeDescendants = false,
    string[]? IntakeStatuses = null,
    // "All" or "Epics" (case-insensitive); null = All.
    string? ScopeMode = null,
    // Preview: the board intake is limited to (Jira board id / Azure DevOps team id); GitHub: the
    // project (node id) — also for statuses, fields and repos.
    string? BoardId = null,
    string? BoardName = null,
    // GitHub: the single-select field holding the status (statuses, preview); null = "Status".
    string? StatusField = null,
    // GitHub preview: only issues from these repositories (owner/name).
    string[]? RepoFilter = null);

/// <summary>Supported=false means the caller's runner could not answer — offline, or too old
/// for tracker connections. The settings page says so instead of showing an error.</summary>
/// <param name="Defaults">For a successful <c>statuses</c> query: the status map and intake a new
/// ticket source starts from (<see cref="TrackerStatusDefaults"/>).</param>
public sealed record TrackerQueryResponse(bool Supported, TrackerQueryResult? Result = null, TrackerDefaultsDto? Defaults = null);

public sealed record TrackerDefaultsDto(
    Features.ProjectConfig.TrackerStatusMapDto StatusMap, string[] IntakeStatuses);

/// <summary>
/// Asks the tracker something on the caller's behalf, through the caller's own runner — the
/// only place the token exists. What the "Ticket source" settings are built from: test the
/// connection, list projects, list a project's boards, search epics, list statuses, preview what
/// would be picked up.
/// </summary>
public sealed class TrackerQueryEndpoint(
    TrackersDbContext db,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<TrackerQueryEndpoint> logger)
    : Endpoint<TrackerQueryBody, TrackerQueryResponse>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> Kinds =
    [
        TrackerQueryKinds.Test, TrackerQueryKinds.Scopes, TrackerQueryKinds.Epics,
        TrackerQueryKinds.Statuses, TrackerQueryKinds.Preview, TrackerQueryKinds.Boards,
        TrackerQueryKinds.Fields, TrackerQueryKinds.Repos,
    ];

    public override void Configure() => Post("/api/trackers/connections/{connectionId:ulid}/query");

    public override async Task HandleAsync(TrackerQueryBody req, CancellationToken ct)
    {
        if (!Kinds.Contains(req.Kind))
        {
            ThrowError($"Unknown query '{req.Kind}'.");
            return;
        }

        var connectionId = Ulid.Parse(Route<string>("connectionId"));
        // Owner only, and 404 rather than 403 for anyone else's: a query runs on the owner's
        // machine with the owner's token.
        var connection = await db.Connections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == currentUser.UserId, ct);
        if (connection is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new TrackerQueryResponse(false), ct);
            return;
        }

        var query = new TrackerQueryRequest(
            connection.ConnectionKey, req.Kind, req.Scope?.Trim(), req.Search?.Trim(),
            req.EpicKeys, string.IsNullOrWhiteSpace(req.ExtraClause) ? null : req.ExtraClause.Trim(),
            req.IncludeDescendants,
            req.IntakeStatuses?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray(),
            string.Equals(req.ScopeMode?.Trim(), "epics", StringComparison.OrdinalIgnoreCase)
                ? TrackerScopeModes.Epics
                : TrackerScopeModes.All,
            string.IsNullOrWhiteSpace(req.BoardId) ? null : req.BoardId.Trim(),
            string.IsNullOrWhiteSpace(req.BoardId) || string.IsNullOrWhiteSpace(req.BoardName) ? null : req.BoardName.Trim(),
            string.IsNullOrWhiteSpace(req.StatusField) ? null : req.StatusField.Trim(),
            req.RepoFilter?.Where(Features.ProjectConfig.ProjectTrackerMappers.IsRepoName).Select(r => r.Trim()).ToArray());

        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/tracker-query",
                new RelayTrackerQueryRequest(currentUser.OrgId.ToString(), relay.UserId, query),
                Json, ct);
            response.EnsureSuccessStatusCode();
            var relayed = await response.Content.ReadFromJsonAsync<RelayTrackerQueryResponse>(Json, ct);

            if (relayed is not { Supported: true, Result: not null })
            {
                await Send.OkAsync(new TrackerQueryResponse(false), ct);
                return;
            }

            // A test refreshes what the board shows about the connection without waiting for
            // the runner's next Hello.
            if (req.Kind == TrackerQueryKinds.Test && relayed.Result.Connection is { } tested)
            {
                connection.Healthy = tested.Healthy;
                connection.HealthDetail = tested.HealthDetail;
                connection.AccountId = tested.AccountId ?? connection.AccountId;
                connection.DisplayName = tested.DisplayName ?? connection.DisplayName;
                connection.CheckedAt = tested.CheckedAt ?? DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            TrackerDefaultsDto? defaults = null;
            if (req.Kind == TrackerQueryKinds.Statuses && relayed.Result is { Ok: true, Statuses: { } statuses })
            {
                var computed = TrackerStatusDefaults.Compute(connection.Kind, statuses);
                defaults = new TrackerDefaultsDto(
                    Features.ProjectConfig.TrackerStatusMapDto.From(computed.StatusMap),
                    computed.IntakeStatuses);
            }

            await Send.OkAsync(new TrackerQueryResponse(true, relayed.Result, defaults), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Tracker query relay failed for connection {ConnectionId}", connectionId);
            await Send.OkAsync(new TrackerQueryResponse(false), ct);
        }
    }
}

/// <summary>A connection typed into the settings page. Its token is relayed to the caller's own
/// runner and goes nowhere else: not stored, not logged, not echoed, not audited.</summary>
/// <param name="Kind">"jira", "ado" or "github".</param>
/// <param name="Site">Jira site (<c>acme.atlassian.net</c>), Azure DevOps organization, or the
/// GitHub host (<c>github.com</c> — the default when empty — or <c>acme.ghe.com</c>).</param>
/// <param name="Token">The token; empty for a GitHub connection that uses the gh login.</param>
/// <param name="AuthMode">GitHub: "gh" (the developer's gh login, the default) or "token".</param>
public sealed record AddTrackerConnectionBody(string Kind, string? Site, string? Email, string? Token, string? AuthMode = null)
{
    public override string ToString() => $"AddTrackerConnectionBody {{ Kind = {Kind}, Site = {Site}, AuthMode = {AuthMode}, Token = *** }}";
}

/// <summary>Supported=false: the caller's runner is offline or too old. Ok=false: the tracker
/// refused the token (Error says why, scrubbed by the runner). Never the token.</summary>
public sealed record AddTrackerConnectionResponse(bool Supported, bool Ok, string? Error = null, TrackerConnectionResponse? Connection = null);

public sealed record RemoveTrackerConnectionResponse(bool Supported, bool Ok, string? Error = null);

public sealed class AddTrackerConnectionValidator : Validator<AddTrackerConnectionBody>
{
    public AddTrackerConnectionValidator()
    {
        // Messages never quote what was sent: the token must not come back in a 400.
        RuleFor(x => x.Kind).Must(k => k is TrackerKinds.Jira or TrackerKinds.AzureDevOps or TrackerKinds.GitHub)
            .WithMessage("Pick Jira Cloud (jira), Azure DevOps (ado) or GitHub (github).");
        // WithMessage covers only the rule right before it, so each rule carries its own.
        RuleFor(x => x.Site)
            .NotEmpty().WithMessage("Give the Jira site or the Azure DevOps organization.")
            .When(x => x.Kind != TrackerKinds.GitHub);
        RuleFor(x => x.Site).MaximumLength(300)
            .WithMessage(x => x.Kind == TrackerKinds.GitHub
                ? "Give github.com or your GHE.com host."
                : "Give the Jira site or the Azure DevOps organization.");
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Give the email of your Atlassian account.")
            .MaximumLength(320).WithMessage("Give the email of your Atlassian account.")
            .When(x => x.Kind == TrackerKinds.Jira);
        RuleFor(x => x.AuthMode).Must(m => m is null or TrackerAuthModes.GhCli or TrackerAuthModes.Token)
            .When(x => x.Kind == TrackerKinds.GitHub)
            .WithMessage("Use your gh login (gh) or a token (token).");
        // WithMessage covers only the rule right before it — the empty case needs its own.
        RuleFor(x => x.Token).NotEmpty().WithMessage("Paste the token.")
            .When(x => x.Kind != TrackerKinds.GitHub || x.AuthMode == TrackerAuthModes.Token);
        RuleFor(x => x.Token).MaximumLength(4000)
            .WithMessage("Paste the token.");
    }
}

/// <summary>
/// Adds a tracker connection from the settings page. The host only relays: the token travels
/// over loopback to the caller's own runner (<c>AddTrackerConnection</c>), which tests it and
/// stores it in the OS secret store; what comes back — and what this endpoint stores and
/// answers — is the connection's metadata. The one host → runner field that carries a
/// credential, at the developer's own request.
/// </summary>
public sealed class AddTrackerConnectionEndpoint(
    TrackersDbContext db,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog audit,
    ILogger<AddTrackerConnectionEndpoint> logger)
    : Endpoint<AddTrackerConnectionBody, AddTrackerConnectionResponse>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override void Configure() => Post("/api/trackers/connections");

    public override async Task HandleAsync(AddTrackerConnectionBody req, CancellationToken ct)
    {
        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new AddTrackerConnectionResponse(false, false), ct);
            return;
        }

        RelayAddTrackerConnectionResponse? relayed;
        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            using var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/tracker-connections",
                new RelayAddTrackerConnectionRequest(currentUser.OrgId.ToString(), relay.UserId,
                    new AddTrackerConnectionRequest(req.Kind, (req.Site ?? "").Trim(), req.Email?.Trim(), req.Token ?? "",
                        req.Kind == TrackerKinds.GitHub ? req.AuthMode ?? TrackerAuthModes.GhCli : null)),
                Json, ct);
            response.EnsureSuccessStatusCode();
            relayed = await response.Content.ReadFromJsonAsync<RelayAddTrackerConnectionResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The type only: an exception message could quote the request.
            logger.LogWarning("Tracker connection relay failed: {Error}", ex.GetType().Name);
            await Send.OkAsync(new AddTrackerConnectionResponse(false, false), ct);
            return;
        }

        if (relayed is not { Supported: true, Result: { } result })
        {
            await Send.OkAsync(new AddTrackerConnectionResponse(false, false), ct);
            return;
        }
        if (!result.Ok || result.Connection is not { } added)
        {
            await Send.OkAsync(new AddTrackerConnectionResponse(true, false, result.Error), ct);
            return;
        }

        // Shown at once, without waiting for the runner's next Hello (which says the same).
        var machine = relayed.MachineName ?? "this machine";
        var row = await db.Connections.FirstOrDefaultAsync(
            c => c.UserId == currentUser.UserId && c.ConnectionKey == added.Id, ct);
        if (row is null)
        {
            row = new TrackerConnection
            {
                Id = Ulid.NewUlid(),
                OrgId = currentUser.OrgId,
                UserId = currentUser.UserId,
                ConnectionKey = added.Id,
                Kind = added.Kind,
                SiteUrl = added.SiteUrl,
                MachineName = machine,
            };
            db.Connections.Add(row);
        }
        row.Kind = added.Kind;
        row.AuthMode = added.AuthMode;
        row.SiteUrl = added.SiteUrl;
        row.AccountId = added.AccountId;
        row.DisplayName = added.DisplayName;
        row.Healthy = added.Healthy;
        row.HealthDetail = added.HealthDetail;
        row.CheckedAt = added.CheckedAt;
        row.MachineName = machine;
        row.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(Orkka.Modules.Agents.Contracts.AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, Orkka.Modules.Agents.Contracts.AgentAuditEventType.TrackerConnectionChanged,
            $"Tracker connection added: {added.Kind} {added.SiteUrl} on {machine}",
            metadata: new { connection = added.Id, added.Kind, added.SiteUrl, machine }), ct);

        await Send.OkAsync(new AddTrackerConnectionResponse(true, true, null, row.ToResponse()), ct);
    }
}

/// <summary>
/// Removes a tracker connection — its token from the runner's secret store and its entry from
/// trackers.json — through the caller's own runner. The row goes too unless a project's ticket
/// source still points at it; then it stays, marked removed, so the project shows why it stopped.
/// </summary>
public sealed class RemoveTrackerConnectionEndpoint(
    TrackersDbContext db,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    Orkka.Modules.Agents.Contracts.IAgentAuditLog audit,
    ILogger<RemoveTrackerConnectionEndpoint> logger)
    : EndpointWithoutRequest<RemoveTrackerConnectionResponse>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override void Configure() => Delete("/api/trackers/connections/{connectionId:ulid}");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var connectionId = Ulid.Parse(Route<string>("connectionId"));
        var row = await db.Connections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == currentUser.UserId, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new RemoveTrackerConnectionResponse(false, false), ct);
            return;
        }

        RelayRemoveTrackerConnectionResponse? relayed;
        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            using var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/tracker-connections/remove",
                new RelayRemoveTrackerConnectionRequest(currentUser.OrgId.ToString(), relay.UserId, row.ConnectionKey),
                Json, ct);
            response.EnsureSuccessStatusCode();
            relayed = await response.Content.ReadFromJsonAsync<RelayRemoveTrackerConnectionResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Tracker connection removal relay failed for {ConnectionId}", connectionId);
            await Send.OkAsync(new RemoveTrackerConnectionResponse(false, false), ct);
            return;
        }

        if (relayed is not { Supported: true, Result: { } result })
        {
            await Send.OkAsync(new RemoveTrackerConnectionResponse(false, false), ct);
            return;
        }
        if (!result.Ok)
        {
            await Send.OkAsync(new RemoveTrackerConnectionResponse(true, false, result.Error), ct);
            return;
        }

        var inUse = await db.ProjectConfigs.AnyAsync(c => c.TrackerConnectionId == row.Id, ct);
        if (inUse)
        {
            row.Healthy = false;
            row.HealthDetail = $"Removed from {relayed.MachineName ?? row.MachineName} — add it again to keep picking up tickets.";
        }
        else
        {
            db.Connections.Remove(row);
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(Orkka.Modules.Agents.Contracts.AgentAuditRecord.ByUser(
            currentUser.OrgId, currentUser.UserId, Orkka.Modules.Agents.Contracts.AgentAuditEventType.TrackerConnectionChanged,
            $"Tracker connection removed: {row.Kind} {row.SiteUrl}",
            metadata: new { connection = row.ConnectionKey, row.Kind, row.SiteUrl }), ct);

        await Send.OkAsync(new RemoveTrackerConnectionResponse(true, true), ct);
    }
}

// Wire shapes mirroring the Worker's RunnerRelayEndpoints — duplicated on purpose, as the
// other relay callers do, so the API host takes no reference on the Worker.
file sealed record RelayTrackerQueryRequest(string OrgId, string UserId, TrackerQueryRequest Query);

file sealed record RelayTrackerQueryResponse(bool Supported, TrackerQueryResult? Result);

file sealed record RelayAddTrackerConnectionRequest(string OrgId, string UserId, AddTrackerConnectionRequest Connection)
{
    public override string ToString() => $"RelayAddTrackerConnectionRequest {{ OrgId = {OrgId}, Connection = {Connection} }}";
}

file sealed record RelayAddTrackerConnectionResponse(bool Supported, AddTrackerConnectionResult? Result, string? MachineName);

file sealed record RelayRemoveTrackerConnectionRequest(string OrgId, string UserId, string ConnectionId);

file sealed record RelayRemoveTrackerConnectionResponse(bool Supported, RemoveTrackerConnectionResult? Result, string? MachineName);
