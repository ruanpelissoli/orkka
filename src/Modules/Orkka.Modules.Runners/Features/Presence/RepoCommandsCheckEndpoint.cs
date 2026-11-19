namespace Orkka.Modules.Runners.Features.Presence;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.SharedKernel.Security;

/// <param name="Kind">"setup", "check" or "build".</param>
/// <param name="Allowed">It would run on the caller's machine now.</param>
/// <param name="Source">"declared" (the repository's orkka.json on its default branch) or
/// "approved" (on that machine); null when not allowed.</param>
/// <param name="ApprovalId">Not allowed: the id to approve it by, at that machine's terminal
/// (<c>./orkka commands approve &lt;id&gt;</c>).</param>
/// <param name="Error">The machine could not check it (repository not prepared there yet).</param>
public sealed record RepoCommandStatus(
    string Kind, string Command, bool Allowed, string? Source, string? ApprovalId, string? Machine, string? Error);

public sealed record RepoCommandsStatus(string RepoId, string Name, RepoCommandStatus[] Commands);

/// <summary>Supported=false: the caller's runner is offline or too old to check commands.</summary>
public sealed record RepoCommandsCheckResponse(bool Supported, RepoCommandsStatus[] Repos, string? Error = null);

/// <summary>
/// "Will my machine run these repositories' setup, check and build commands?" — asked during
/// project setup, before a task needs them. Each command set on a repository (the ones in
/// <c>orkka.json</c> need no approval) is vetted by CommandTrust on the caller's own runner;
/// one that is neither declared nor approved is recorded there as pending, and the answer gives
/// the id the developer approves it by at that machine's terminal. Nothing can approve a command
/// from here (SECURITY.md → Repository commands).
/// </summary>
public sealed class RepoCommandsCheckEndpoint(
    IProjectResolver projectResolver,
    IProjectsApi projectsApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<RepoCommandsCheckEndpoint> logger)
    : EndpointWithoutRequest<RepoCommandsCheckResponse>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override void Configure() => Post("/api/projects/{projectId}/setup/commands");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await projectResolver.ResolveAsync(Route<string>("projectId")!, ct) is not { } projectId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var repos = await projectsApi.ListReposAsync(projectId, ct);
        var commands = repos.SelectMany(r => CommandsOf(r).Select(c => new RepoCommandToVet(r.Id.ToString(), c.Kind, c.Command)))
            .ToArray();
        if (commands.Length == 0)
        {
            await Send.OkAsync(new RepoCommandsCheckResponse(true, [.. repos.Select(r => new RepoCommandsStatus(r.Id.ToString(), r.Name, []))]), ct);
            return;
        }

        var relay = await relayLocator.LocateAsync(currentUser.OrgId, currentUser.UserId, ct);
        if (relay is null)
        {
            await Send.OkAsync(new RepoCommandsCheckResponse(false, []), ct);
            return;
        }

        RelayVetCommandsResponse? result;
        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            using var response = await client.PostAsJsonAsync(
                $"{relay.BaseUrl}/internal/runner/repo-commands/vet",
                new RelayVetCommandsRequest(currentUser.OrgId.ToString(), relay.UserId, commands),
                Json, ct);
            response.EnsureSuccessStatusCode();
            result = await response.Content.ReadFromJsonAsync<RelayVetCommandsResponse>(Json, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Repository command check relay failed for org {OrgId}", currentUser.OrgId);
            await Send.OkAsync(new RepoCommandsCheckResponse(false, []), ct);
            return;
        }

        if (result is not { Supported: true })
        {
            await Send.OkAsync(new RepoCommandsCheckResponse(false, []), ct);
            return;
        }

        var statuses = repos.Select(repo => new RepoCommandsStatus(
            repo.Id.ToString(), repo.Name,
            [.. CommandsOf(repo).Select(c =>
            {
                var vetted = result.Results.FirstOrDefault(v => v.RepoId == repo.Id.ToString() && v.Kind == c.Kind);
                return vetted is null
                    ? new RepoCommandStatus(KindName(c.Kind), c.Command, false, null, null, null,
                        "Your runner did not answer for this command.")
                    : new RepoCommandStatus(KindName(c.Kind), vetted.Command, vetted.Allowed, vetted.Source,
                        vetted.Approval?.Id, vetted.Approval?.Machine, vetted.Error);
            })])).ToArray();

        await Send.OkAsync(new RepoCommandsCheckResponse(true, statuses, result.Error), ct);
    }

    /// <summary>The commands a repository's settings hold. Empty settings fall back to its
    /// orkka.json, which needs no approval — so there is nothing to vet for them.</summary>
    internal static IEnumerable<(CommandKind Kind, string Command)> CommandsOf(RepoSummary repo)
    {
        if (!string.IsNullOrWhiteSpace(repo.SetupCommand))
            yield return (CommandKind.Setup, repo.SetupCommand.Trim());
        if (!string.IsNullOrWhiteSpace(repo.CheckCommand))
            yield return (CommandKind.Check, repo.CheckCommand.Trim());
        if (!string.IsNullOrWhiteSpace(repo.BuildCommand))
            yield return (CommandKind.Build, repo.BuildCommand.Trim());
    }

    internal static string KindName(CommandKind kind) => kind.ToString().ToLowerInvariant();
}

// Wire shapes mirroring the host's RunnerRelayEndpoints, file-scoped as the other relay callers do.
file sealed record RelayVetCommandsRequest(string OrgId, string UserId, RepoCommandToVet[] Commands);

file sealed record RelayVetCommandsResponse(bool Supported, VettedRepoCommand[] Results, string? Error = null);
