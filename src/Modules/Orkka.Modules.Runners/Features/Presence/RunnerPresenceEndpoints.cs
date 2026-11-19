namespace Orkka.Modules.Runners.Features.Presence;

using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Runners.Infrastructure;
using Orkka.SharedKernel.Security;

public sealed record RunnerProviderDto(
    string Provider,
    bool CliFound,
    bool Authenticated,
    string[]? Models,
    string? Detail,
    string? AuthMode = null,
    string? AuthDetail = null,
    string? CliVersion = null);

public sealed record MyRunnerResponse(
    bool Online,
    string? MachineName,
    string? DaemonVersion,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSeenAt,
    RunnerProviderDto[]? Providers,
    bool HasActiveTokens,
    // Which GitHub account this machine pushes as. Null from older runners.
    RunnerGitIdentityDto? GitIdentity = null,
    // What the connected daemon can do ("app-run", "editor", …) — the UI gates feature
    // buttons on these instead of failing the click. Null from older runners.
    string[]? Capabilities = null,
    // Provider readiness that survives disconnect: mirrors Providers while online, else the
    // last Hello this user's runner ever sent. The task form annotates models from it so a
    // reconnecting runner doesn't blank the "runs on your runner / billed" hints.
    RunnerProviderDto[]? LastKnownProviders = null,
    DateTimeOffset? LastKnownAt = null,
    string? LastKnownMachineName = null,
    // gh / az / glab on this machine — what opens the developer's pull requests. Null from
    // older runners.
    RunnerForgeCliDto[]? ForgeClis = null,
    // Repository commands this runner refused and that wait for `./orkka commands approve
    // <id>` on that machine — never approvable from here. Empty when there are none.
    PendingCommandApprovalDto[]? PendingCommandApprovals = null,
    // Who this workspace's commits are authored as. Null from older runners.
    RunnerCommitIdentityDto? CommitIdentity = null);

/// <summary>Mirrors RunnerCommitIdentity. Source: "workspace" | "git-config" | "none".</summary>
public sealed record RunnerCommitIdentityDto(string? Name, string? Email, string Source);

/// <summary>Mirrors CommandApproval; Kind is the CommandKind name.</summary>
public sealed record PendingCommandApprovalDto(string Id, string Repo, string Command, string Kind, string Machine);

/// <summary>Mirrors RunnerForgeCliStatus.</summary>
public sealed record RunnerForgeCliDto(
    string Cli, bool Found, bool Authenticated, string[]? Accounts, string? Detail);

/// <summary>Mirrors RunnerGitIdentity so an older snapshot deserializes with nulls.</summary>
public sealed record RunnerGitIdentityDto(
    string? DefaultAccount,
    bool ScopedPerRepoPath,
    RunnerRepoGitIdentityDto[]? Repos,
    string? Detail);

public sealed record RunnerRepoGitIdentityDto(string? RepoUrl, string? Account);

public sealed record OrgRunnerResponse(
    string UserId,
    bool Online,
    string MachineName,
    string? DaemonVersion,
    DateTimeOffset LastSeenAt,
    RunnerProviderDto[] Providers);

internal static class ProviderJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // ProvidersJson is a RunnerProviderStatus[] snapshot; the DTO mirrors its shape, so
    // a straight deserialize works and tolerates older snapshots (missing props → null).
    public static RunnerProviderDto[] Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RunnerProviderDto[]>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The wire shape, for callers that reason about readiness rather than render it.</summary>
    public static RunnerProviderStatus[] ParseStatuses(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RunnerProviderStatus[]>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static RunnerForgeCliDto[]? ParseForgeClis(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RunnerForgeCliDto[]>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static RunnerGitIdentityDto? ParseGitIdentity(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RunnerGitIdentityDto>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static PendingCommandApprovalDto[] ParsePendingApprovals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<PendingCommandApprovalDto[]>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static RunnerCommitIdentityDto? ParseCommitIdentity(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RunnerCommitIdentityDto>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string[]? ParseCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The caller's own runner: presence + provider readiness for the "My Runner"
/// settings page. Poll-friendly (DB read, no hub state).</summary>
public sealed class GetMyRunnerEndpoint(
    RunnersDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<MyRunnerResponse>
{
    public override void Configure()
    {
        Get("/api/me/runner");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var presence = await db.RunnerPresences.AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.OrgId == currentUser.OrgId && p.UserId == currentUser.UserId, ct);
        var hasTokens = await db.RunnerTokens.AsNoTracking()
            .AnyAsync(t => t.OrgId == currentUser.OrgId && t.UserId == currentUser.UserId
                           && t.RevokedAt == null, ct);

        var online = presence is not null
            && DateTimeOffset.UtcNow - presence.LastSeenAt < RunnerPresenceRules.OnlineWindow;

        var snapshot = online ? null : await db.RunnerProviderSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.OrgId == currentUser.OrgId && s.UserId == currentUser.UserId, ct);
        RunnerProviderDto[]? lastKnown = online && presence is not null
            ? ProviderJson.Parse(presence.ProvidersJson)
            : snapshot is null ? null : ProviderJson.Parse(snapshot.ProvidersJson);
        var lastKnownAt = online ? presence?.LastSeenAt : snapshot?.CapturedAt;
        var lastKnownMachine = online ? presence?.MachineName : snapshot?.MachineName;

        await Send.OkAsync(new MyRunnerResponse(
            online,
            presence?.MachineName,
            presence?.DaemonVersion,
            presence?.ConnectedAt,
            presence?.LastSeenAt,
            presence is null ? null : ProviderJson.Parse(presence.ProvidersJson),
            hasTokens,
            ProviderJson.ParseGitIdentity(presence?.GitIdentityJson),
            ProviderJson.ParseCapabilities(presence?.CapabilitiesJson),
            lastKnown,
            lastKnownAt,
            lastKnownMachine,
            ProviderJson.ParseForgeClis(presence?.ForgeClisJson),
            ProviderJson.ParsePendingApprovals(presence?.PendingCommandApprovalsJson),
            ProviderJson.ParseCommitIdentity(presence?.CommitIdentityJson)), ct);
    }
}

/// <summary>Org-wide runner roster for managers: who has a runner connected, from which
/// machine, with which providers ready. Reads are open to all org members, matching the
/// platform's read model.</summary>
public sealed class ListOrgRunnersEndpoint(
    RunnersDbContext db,
    ICurrentUser currentUser)
    : EndpointWithoutRequest<List<OrgRunnerResponse>>
{
    public override void Configure()
    {
        Get("/api/org/runners");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var rows = await db.RunnerPresences.AsNoTracking()
            .Where(p => p.OrgId == currentUser.OrgId)
            .OrderByDescending(p => p.LastSeenAt)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var response = rows.Select(p => new OrgRunnerResponse(
            p.UserId,
            now - p.LastSeenAt < RunnerPresenceRules.OnlineWindow,
            p.MachineName,
            p.DaemonVersion,
            p.LastSeenAt,
            ProviderJson.Parse(p.ProvidersJson))).ToList();

        await Send.OkAsync(response, ct);
    }
}
