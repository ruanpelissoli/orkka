namespace Orkka.Modules.Agents.Features.AgentChat;

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Runners.Contracts;

/// <summary>
/// Hosted chat routing: every turn relays to the Worker pod holding the requester's runner
/// connection — their machine, their AI subscription. There is no in-process fallback on
/// Orkka's key (customer AI never runs on it, user decision 2026-10-26): a requester whose
/// runner is offline, or a turn with no requester to route by, gets an explicit error.
/// </summary>
public sealed class HybridChatAgentInvoker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<HybridChatAgentInvoker> logger) : IChatAgentInvoker
{
    public const string HttpClientName = "runner-relay";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // e.g. "http://{pod}.orkka-orkka-worker:8080" — {pod} is the Worker pod name from
    // the presence row. Empty = relay not configured; MyRunner chat gets an actionable
    // error instead of a wrong route.
    private readonly string? _relayUrlTemplate = configuration["Runners:RelayUrlTemplate"];

    /// <summary>Every turn is relayed, and a relayed turn's tools depend on the capability its
    /// runner advertises — <c>ChatTurnPreparer</c> asks presence for that, not this.</summary>
    public bool SupportsChatTools => false;

    public async Task<string?> GetRouteBlockerAsync(CancellationToken ct = default)
    {
        try
        {
            await ResolveRelayAsync(ct);
            return null;
        }
        catch (RunnerRouteUnavailableException ex)
        {
            return ex.Message;
        }
    }

    public async Task<ChatWorkspacePreparation> EnsureChatWorkspaceAsync(
        Ulid orgId, Ulid projectId, IReadOnlyList<ChatWorkspaceRepo> repos,
        Ulid? taskId = null, CancellationToken ct = default)
    {
        RelayTarget relay;
        try
        {
            relay = await ResolveRelayAsync(ct);
        }
        catch (RunnerRouteUnavailableException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var payload = new RelayWorkspaceRequest(
            orgId.ToString(), relay.UserId,
            repos.Select(r => new RelayWorkspaceRepo(r.Id.ToString(), r.Name, r.DefaultBranch, r.GitUrl, r.Branch)).ToArray(),
            projectId.ToString(),
            taskId?.ToString());

        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.PostAsJsonAsync(
            $"{relay.BaseUrl}/internal/runner/chat-workspace", payload, Json, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RelayWorkspaceResponse>(Json, ct);
        if (result?.Error is not null || result?.WorkspaceToken is null)
            throw new InvalidOperationException(
                result?.Error ?? "Chat workspace preparation failed on your runner.");

        // Prefer the detailed list; an older Worker sends only ids, and a repo we cannot
        // name or explain is still better reported as unavailable than silently included.
        var unavailable = result.UnavailableRepos is { Length: > 0 } detailed
            ? [.. detailed
                .Select(r => (Parsed: Ulid.TryParse(r.RepoId, out var id), Id: id, Wire: r))
                .Where(x => x.Parsed)
                .Select(x => new UnavailableChatRepo(
                    x.Id,
                    x.Wire.Name,
                    // By name, so the two sides can deploy in either order.
                    Enum.TryParse<RepoAccessFailureReason>(x.Wire.Reason, out var reason)
                        ? reason
                        : RepoAccessFailureReason.Unknown,
                    x.Wire.Detail,
                    // Reaching this branch at all means the turn was relayed to the
                    // requester's own runner, so the clone that failed was theirs.
                    OnUserMachine: true))]
            : ParseLegacyIds(result.UnavailableRepoIds, repos);

        return new ChatWorkspacePreparation(result.WorkspaceToken, unavailable);
    }

    /// <summary>The pre-reason wire form: ids only. The repo is still excluded and still
    /// named — the name comes back from the request, since the caller knows what it asked
    /// for — but there is nothing to tell the user beyond "it couldn't be opened".</summary>
    private static List<UnavailableChatRepo> ParseLegacyIds(
        string[]? ids, IReadOnlyList<ChatWorkspaceRepo> requested)
    {
        var unavailable = new List<UnavailableChatRepo>(ids?.Length ?? 0);
        foreach (var raw in ids ?? [])
        {
            if (!Ulid.TryParse(raw, out var id))
                continue;
            unavailable.Add(new UnavailableChatRepo(
                id,
                requested.FirstOrDefault(r => r.Id == id)?.Name ?? "a repo",
                RepoAccessFailureReason.Unknown,
                OnUserMachine: true));
        }
        return unavailable;
    }

    /// <param name="toolContext">Non-null when this turn carries platform tools: the identity
    /// the Worker rebuilds the tool server from.</param>
    public async IAsyncEnumerable<ChatChunk> InvokeAgentAsync(
        string workspacePath, string systemPrompt, string userMessage, string model,
        string? resumeSessionId = null, Ulid? conversationId = null,
        IChatToolServer? toolServer = null,
        ChatToolServerContext? toolContext = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        RelayTarget relay = default;
        string? routeError = null;
        try
        {
            relay = await ResolveRelayAsync(ct);
        }
        catch (RunnerRouteUnavailableException ex)
        {
            routeError = ex.Message;
        }

        if (routeError is not null)
        {
            yield return new ChatChunk(ChatChunkType.Error, routeError);
            yield return new ChatChunk(ChatChunkType.Done, "");
            yield break;
        }

        // Relayed turns execute on the user's machine, out of reach of THIS process's tool
        // server — so the identity travels instead and the Worker rebuilds the same server
        // against the same database, with the runner forwarding each call to it. A turn whose
        // tools were enabled without an identity to rebuild from would advertise tools that
        // cannot be answered, so it relays without them rather than guessing.
        if (toolServer is not null && toolContext is null)
            logger.LogWarning(
                "[ChatRelay] Tool server attached without a tool context — relaying the turn without tools");

        var payload = new RelayChatRequest(
            relay.OrgId.ToString(), relay.UserId,
            workspacePath, systemPrompt, userMessage, model, resumeSessionId,
            ToolsEnabled: toolContext is not null,
            ProjectId: toolContext?.ProjectId.ToString(),
            AgentId: toolContext?.AgentId.ToString(),
            ConversationId: toolContext?.ConversationId.ToString(),
            AssistantMessageId: toolContext?.AssistantMessageId.ToString(),
            RequesterLocale: toolContext?.RequesterLocale,
            AgentRole: toolContext?.AgentRole.ToString(),
            DesignFolderPath: toolContext?.DesignFolderPath,
            ReadOnlyDirs: await ReadOnlyDirsForAsync(workspacePath, ct));

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{relay.BaseUrl}/internal/runner/chat")
        {
            Content = JsonContent.Create(payload, options: Json),
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // NDJSON: one ChatChunk per line, streamed as the runner produces them.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var chunk = JsonSerializer.Deserialize<ChatChunk>(line, Json);
            if (chunk is null)
                continue;
            // Re-stamped here rather than trusted from the wire: reaching this loop at all
            // means the turn was relayed to the requester's runner, and a Worker older than
            // the flag would otherwise report a $0 turn as billable.
            yield return chunk with { ServedByRunner = true };
            if (chunk.Type == ChatChunkType.Done)
                yield break;
        }
    }

    /// <summary>
    /// The project's base folder when it is a folder of repositories: the chat agent may read
    /// the developer's sibling checkouts there for reference (its workspace already holds clones
    /// of every repo of the project). A monorepo adds nothing — its one repository is already in
    /// the workspace. Null on anything unexpected: the turn simply runs without it.
    /// </summary>
    private async Task<string[]?> ReadOnlyDirsForAsync(string workspaceToken, CancellationToken ct)
    {
        if (!Orkka.LocalRunner.Contracts.RunnerTokens.TryParseChatWorkspace(workspaceToken, out _, out var rawProjectId)
            || !Ulid.TryParse(rawProjectId, out var projectId))
            return null;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var projects = scope.ServiceProvider.GetService<Orkka.Modules.Projects.Contracts.IProjectsApi>();
            var project = projects is null ? null : await projects.GetProjectSummaryAsync(projectId, ct);
            return project is { BaseFolderShape: Orkka.Modules.Projects.Contracts.BaseFolderShape.MultiRepo, BaseFolder: { Length: > 0 } baseFolder }
                ? [baseFolder]
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "[ChatRelay] Could not resolve the base folder of project {ProjectId}", projectId);
            return null;
        }
    }

    /// <summary>The requester's runner. Throws <see cref="RunnerRouteUnavailableException"/>
    /// when there is no route (no requester, runner offline, relay unconfigured).</summary>
    private async Task<RelayTarget> ResolveRelayAsync(CancellationToken ct)
    {
        if (ChatRequesterContext.Get() is not { } requester)
            throw new RunnerRouteUnavailableException(
                "This chat turn has no member to run it for, and chat runs only on a member's own runner.");

        using var scope = scopeFactory.CreateScope();
        var fleet = scope.ServiceProvider.GetService<IRunnerFleet>();
        var presence = fleet is null
            ? null
            : await fleet.GetPresenceAsync(requester.OrgId, requester.UserId, ct);
        if (presence is not { Online: true })
        {
            throw new RunnerRouteUnavailableException(
                "Your runner is offline — chat runs on your machine with your own AI subscription. " +
                "Start Orkka with `./orkka start` and try again.");
        }

        if (string.IsNullOrWhiteSpace(_relayUrlTemplate))
        {
            logger.LogWarning(
                "[ChatRelay] Runners:RelayUrlTemplate not configured — cannot reach pod {Pod}",
                presence.WorkerInstance);
            throw new RunnerRouteUnavailableException(
                "Chat is not available on this deployment yet (the runner relay is not configured) — " +
                "ask your administrator.");
        }

        return new RelayTarget(
            requester.OrgId, requester.UserId,
            _relayUrlTemplate.Replace("{pod}", presence.WorkerInstance, StringComparison.Ordinal));
    }

    private readonly record struct RelayTarget(Ulid OrgId, string UserId, string BaseUrl);

    private sealed class RunnerRouteUnavailableException(string message) : Exception(message);
}

// Wire shapes shared with the Worker's internal relay endpoints (duplicated there by
// design — the Worker cannot reference the Agents module's internals; the contract is
// three flat records with web-cased JSON).
// GitUrl is additive for the same rolling-deploy reason as ProjectId below. An older
// Worker drops it and the runner falls back to whatever it already has cloned — the
// pre-fix behavior, not a crash.
// Branch is additive the same way: a task's Discuss chat reads the task branch; absent = the
// default branch, as before.
public sealed record RelayWorkspaceRepo(
    string RepoId, string Name, string? DefaultBranch, string GitUrl = "", string? Branch = null);

// ProjectId is additive (nullable with a default) so mixed-version API/Worker pods keep
// interoperating during a rolling deploy; a missing value falls back to the legacy
// org-level workspace.
// TaskId (additive): a task's Discuss chat gets that task's own workspace on the runner.
public sealed record RelayWorkspaceRequest(
    string OrgId, string UserId, RelayWorkspaceRepo[] Repos, string? ProjectId = null,
    string? TaskId = null);

// Both unavailable-repo fields are additive: an older Worker omits them and the API pod
// treats the preparation as complete, which is exactly how it behaved before they existed.
// UnavailableRepos carries the reason as well, so the turn can tell the user what to fix
// instead of only dropping the repo from the prompt.
public sealed record RelayWorkspaceResponse(
    string? WorkspaceToken, string? Error, string[]? UnavailableRepoIds = null,
    RelayUnavailableRepo[]? UnavailableRepos = null);

/// <summary>Reason travels by NAME so the two sides can be deployed in either order — an
/// unknown name degrades to <c>Unknown</c>, never to a crash.</summary>
public sealed record RelayUnavailableRepo(
    string RepoId, string Name, string Reason, string? Detail);

public sealed record RelayChatRequest(
    string OrgId, string UserId, string WorkspaceToken, string SystemPrompt,
    string UserMessage, string Model, string? ResumeSessionId,
    // Additive: this turn's platform tools. The Worker rebuilds the tool server from these
    // ids (never from anything the model says) and hands its definitions to the runner.
    // Absent/false = the pre-tools behavior: the turn writes fenced blocks the API parses.
    bool ToolsEnabled = false,
    string? ProjectId = null,
    string? AgentId = null,
    string? ConversationId = null,
    string? AssistantMessageId = null,
    string? RequesterLocale = null,
    string? AgentRole = null,
    string? DesignFolderPath = null,
    // Additive: folders the chat agent may read besides its workspace — the project's base
    // folder for a folder-of-repos project. An older Worker drops it and the turn reads only
    // its workspace, as before.
    string[]? ReadOnlyDirs = null);
