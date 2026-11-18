namespace Orkka.Runners.Server.Hosting;

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Execution.Orchestration;
using Orkka.Runners.Server;

// Wire shapes for the API host's chat relay (mirrors the records next to
// HybridChatAgentInvoker — kept flat and web-cased; the two processes deploy together).
// GitUrl lets the runner clone a repo it has never seen; an older API pod omits it and
// the runner keeps its pre-fix behavior of using only what it already has.
public sealed record RelayWorkspaceRepo(
    string RepoId, string Name, string? DefaultBranch, string GitUrl = "", string? Branch = null);

public sealed record RelayWorkspaceRequest(
    string OrgId, string UserId, RelayWorkspaceRepo[] Repos, string? ProjectId = null,
    string? TaskId = null);

// UnavailableRepos supersedes UnavailableRepoIds: the ids alone let the API pod drop the
// repo from the prompt but leave it unable to tell the user WHY, which is the whole point.
// Both are sent — an API pod that predates this reads the ids and behaves as before.
public sealed record RelayWorkspaceResponse(
    string? WorkspaceToken, string? Error, string[]? UnavailableRepoIds = null,
    RelayUnavailableRepo[]? UnavailableRepos = null);

/// <summary>Reason travels by NAME so the two sides can be deployed in either order — an
/// unknown name on the reading end degrades to <c>Unknown</c>, never to a crash.</summary>
public sealed record RelayUnavailableRepo(
    string RepoId, string Name, string Reason, string? Detail);

/// <summary>Asks the user's machine whether git there can actually read these remotes.
/// Supported=false is the usual "no machine can answer" — offline, or a runner too old for
/// the check — which the UI hides rather than reports as a failure.</summary>
public sealed record RelayRepoAccessRequest(
    string OrgId, string UserId, RelayRepoAccessProbe[] Repos);

public sealed record RelayRepoAccessProbe(string RepoId, string GitUrl);

public sealed record RelayRepoAccessResponse(
    bool Supported, RelayRepoAccessResult[] Results, string? Error = null);

public sealed record RelayRepoAccessResult(string RepoId, bool Ok, string? Error);

/// <summary>Mirror of the Agents module's own record (see HybridChatAgentInvoker) — the
/// Worker cannot reference that module's internals, so the contract is duplicated by design.
/// Everything after ResumeSessionId is additive: an API pod that predates chat tools sends
/// none of it and the turn behaves exactly as before.</summary>
public sealed record RelayChatRequest(
    string OrgId, string UserId, string WorkspaceToken, string SystemPrompt,
    string UserMessage, string Model, string? ResumeSessionId,
    bool ToolsEnabled = false,
    string? ProjectId = null,
    string? AgentId = null,
    string? ConversationId = null,
    string? AssistantMessageId = null,
    string? RequesterLocale = null,
    string? AgentRole = null,
    string? DesignFolderPath = null,
    string[]? ReadOnlyDirs = null);

public sealed record RelayEditorsRequest(string OrgId, string UserId);

/// <summary>Supported=false covers every "no editor here" case — runner offline, runner too
/// old — because they all mean the same thing to the UI: hide the action.</summary>
public sealed record RelayEditorsResponse(
    bool Supported, RelayEditorOption[] Editors);

public sealed record RelayEditorOption(string Key, string DisplayName);

public sealed record RelayOpenInEditorRequest(
    string OrgId, string UserId, string WorktreeToken, string? Editor, string? RepoToken = null);

public sealed record RelayOpenInEditorResponse(string? OpenedWith, string? Error);

// ── Utility completions (mirrors the records next to RelayUserRunnerUtility) ──
// Flat on purpose: the runner's UtilityComplete op takes system + one user text anyway
// (RunnerAnthropicClient joins user-role text), so the wire shape is that, not a full
// AnthropicRequest with polymorphic content blocks.

public sealed record RelayUtilityRequest(
    string OrgId, string UserId, string Model, string? System, string UserMessage, int MaxTokens);

/// <summary>Served=false covers every miss — runner offline, no provider signed in, the call
/// failed — because the API host treats them all the same way (its runner-offline path).</summary>
public sealed record RelayUtilityResponse(
    bool Served, string? Text = null,
    int InputTokens = 0, int OutputTokens = 0, int CacheReadTokens = 0, int CacheWriteTokens = 0);

// ── App run (mirrors the records next to the API's AppRunEndpoints) ──────────

public sealed record RelayAppRunTarget(
    string RunId, string Key, string Label, string Kind, string RepoId,
    string Command, string? WorkingDirectory, int? Port, string? Url, string[] DependsOn,
    string? InstallCommand = null, string? StopCommand = null, string? PreviewPath = null,
    bool RestartOnMerge = false);

public sealed record RelayAppRunAttachRequest(string OrgId, string UserId, string ProjectId);

public sealed record RelayTrackerQueryRequest(string OrgId, string UserId, TrackerQueryRequest Query);

/// <summary>Supported=false covers runner-offline and runner-too-old alike.</summary>
public sealed record RelayTrackerQueryResponse(bool Supported, TrackerQueryResult? Result);

/// <summary>Carries a tracker token to the caller's own runner (loopback only, write-only).
/// Its ToString hides the token, like <see cref="AddTrackerConnectionRequest"/>'s.</summary>
public sealed record RelayAddTrackerConnectionRequest(string OrgId, string UserId, AddTrackerConnectionRequest Connection)
{
    public override string ToString() => $"RelayAddTrackerConnectionRequest {{ OrgId = {OrgId}, UserId = {UserId}, Connection = {Connection} }}";
}

/// <summary>Metadata only. MachineName = the runner's machine (from its Hello).</summary>
public sealed record RelayAddTrackerConnectionResponse(bool Supported, AddTrackerConnectionResult? Result, string? MachineName = null);

public sealed record RelayRemoveTrackerConnectionRequest(string OrgId, string UserId, string ConnectionId);

public sealed record RelayRemoveTrackerConnectionResponse(bool Supported, RemoveTrackerConnectionResult? Result, string? MachineName = null);

public sealed record RelayAppRunStartRequest(
    string OrgId, string UserId, string ProjectId,
    RelayAppRunTarget[] Targets, bool ForceFreePorts);

public sealed record RelayAppRunStopRequest(
    string OrgId, string UserId, string ProjectId, string? RunId);

public sealed record RelayAppRunInputRequest(
    string OrgId, string UserId, string RunId, string DataBase64);

public sealed record RelayAppRunResizeRequest(
    string OrgId, string UserId, string RunId, int Cols, int Rows);

/// <summary>Supported=false covers runner-offline and runner-too-old alike; Error carries
/// the actionable sentence when the caller pressed a button (Start) rather than opened a
/// panel (Attach).</summary>
public sealed record RelayAppRunResponse(
    bool Supported,
    Orkka.Runners.Server.AppRun.AppRunSnapshot[] Snapshots,
    string? Error = null);

/// <summary>
/// Cluster-internal relay: the API host forwards a hybrid user's chat operations to THIS
/// pod because it holds their runner's WebSocket (presence row's WorkerInstance). Auth is
/// the existing Worker↔API shared secret (X-Worker-Key == WorkerAuth:ApiKey) — these
/// endpoints must never be exposed through the public runner ingress, which routes only
/// /hubs/runner.
/// </summary>
public static class RunnerRelayEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// This turn's tool server, or null when the API did not enable tools (an older pod, a
    /// non-Anthropic provider, a runner that cannot forward calls) or the identity it sent is
    /// unusable. Every id is parsed strictly: a tool that acted on a half-parsed identity
    /// would write into the wrong conversation, so a bad identity means no tools rather than
    /// a guess. The turn then falls back to fenced blocks, exactly as before.
    /// </summary>
    private static Orkka.Modules.Agents.Contracts.IChatToolServer? BuildChatToolServer(
        RelayChatRequest req, Ulid orgId,
        Orkka.Modules.Agents.Contracts.IChatToolServerFactory factory,
        Channel<ChatChunk> outbound, JsonSerializerOptions json, ILogger logger)
    {
        if (!req.ToolsEnabled)
            return null;

        if (!Ulid.TryParse(req.ProjectId, out var projectId)
            || !Ulid.TryParse(req.AgentId, out var agentId)
            || !Ulid.TryParse(req.ConversationId, out var conversationId)
            || !Ulid.TryParse(req.AssistantMessageId, out var assistantMessageId)
            || !Enum.TryParse<Orkka.Modules.Agents.Contracts.AgentRole>(req.AgentRole, out var role))
        {
            logger.LogWarning(
                "[ChatRelay] Tools requested with an incomplete identity — running the turn without them");
            return null;
        }

        var context = new Orkka.Modules.Agents.Contracts.ChatToolServerContext(
            orgId, projectId, agentId, conversationId, assistantMessageId,
            req.UserId, req.RequesterLocale, role, req.DesignFolderPath);

        return factory.Create(context, payload =>
            // Unbounded channel: never blocks a tool handler, and the write is thread-safe —
            // handlers run on whatever thread the forwarded call arrived on.
            outbound.Writer.TryWrite(new ChatChunk(
                ChatChunkType.Artifact, JsonSerializer.Serialize(payload, json))));
    }

    public static void MapRunnerRelay(this WebApplication app)
    {
        app.MapPost("/internal/runner/chat-workspace", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayWorkspaceRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            // ensureBareClones: the API pod synced these repos onto its own disk, which is
            // not the disk the workspace is built on. Without this the runner finds nothing
            // to make worktrees from and the agent chats in an empty folder. The runner clones
            // with the developer's own git credentials.
            var invoker = new RunnerChatInvoker(
                links.GetLink(orgId, req.UserId),
                loggerFactory.CreateLogger<RunnerChatInvoker>(),
                ensureBareClones: true);
            try
            {
                // ProjectId is absent when an older API pod sent the request (rolling
                // deploy) — Ulid.Empty makes the invoker fall back to the legacy
                // org-level workspace instead of failing the turn.
                var projectId = Ulid.TryParse(req.ProjectId, out var parsed) ? parsed : Ulid.Empty;
                var prepared = await invoker.EnsureChatWorkspaceAsync(
                    orgId,
                    projectId,
                    req.Repos.Select(r => new ChatWorkspaceRepo(
                        Ulid.Parse(r.RepoId), r.Name, r.DefaultBranch, r.GitUrl, r.Branch)).ToList(),
                    Ulid.TryParse(req.TaskId, out var taskId) ? taskId : null,
                    ct);
                return Results.Json(
                    new RelayWorkspaceResponse(
                        prepared.Token, null,
                        prepared.Unavailable.Select(r => r.Id.ToString()).ToArray(),
                        prepared.Unavailable
                            .Select(r => new RelayUnavailableRepo(
                                r.Id.ToString(), r.Name, r.Reason.ToString(), r.Detail))
                            .ToArray()),
                    Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new RelayWorkspaceResponse(null, ex.Message), Json);
            }
        });

        // The preflight for the one precondition nothing else states: a repo is cloned on the
        // developer's machine under their own git credentials, so a machine with none fails
        // their tasks and their chat turns alike. Asking costs one ls-remote per repo; not
        // asking costs a failed run before anyone finds out.
        // A question for the developer's tracker, answered on their machine with the token only
        // that machine holds — what the project's "Ticket source" settings are built from.
        app.MapPost("/internal/runner/tracker-query", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayTrackerQueryRequest>(Json, ct);
            if (req?.Query is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            var (connected, hello) = link.Status;
            if (!connected || hello?.Capabilities?.Contains(RunnerCapabilities.TrackerConfig) is not true)
                return Results.Json(new RelayTrackerQueryResponse(false, null), Json);
            // An older runner would answer "unknown query", or preview the whole project while
            // the page says one board.
            if ((req.Query.Kind == TrackerQueryKinds.Boards || !string.IsNullOrWhiteSpace(req.Query.BoardId))
                && hello.Capabilities.Contains(RunnerCapabilities.TrackerBoards) is not true)
            {
                return Results.Json(new RelayTrackerQueryResponse(true, new TrackerQueryResult(false,
                    "Your runner does not know boards yet — update Orkka (`git pull`, then `./orkka start`).")), Json);
            }

            try
            {
                var result = await link.InvokeAsync<TrackerQueryResult>(
                    RunnerProtocol.TrackerQuery, [req.Query], ct);
                return Results.Json(new RelayTrackerQueryResponse(true, result), Json);
            }
            catch (RunnerUnavailableException)
            {
                return Results.Json(new RelayTrackerQueryResponse(false, null), Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new RelayTrackerQueryResponse(true,
                    new TrackerQueryResult(false, $"Your runner could not ask the tracker: {ex.Message}")), Json);
            }
        });

        // A tracker connection added from the settings page: the token goes to the caller's own
        // runner, which tests and stores it in the OS secret store, and only metadata comes back.
        // Nothing here logs the request, and an exception message is never echoed (it could
        // quote what was sent).
        app.MapPost("/internal/runner/tracker-connections", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayAddTrackerConnectionRequest>(Json, ct);
            if (req?.Connection is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            var (connected, hello) = link.Status;
            if (!connected || hello?.Capabilities?.Contains(RunnerCapabilities.TrackerConnect) is not true)
                return Results.Json(new RelayAddTrackerConnectionResponse(false, null), Json);

            try
            {
                var result = await link.InvokeAsync<AddTrackerConnectionResult>(
                    RunnerProtocol.AddTrackerConnection, [req.Connection], ct);
                return Results.Json(new RelayAddTrackerConnectionResponse(true, result, hello.MachineName), Json);
            }
            catch (RunnerUnavailableException)
            {
                return Results.Json(new RelayAddTrackerConnectionResponse(false, null), Json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Json(new RelayAddTrackerConnectionResponse(true,
                    new AddTrackerConnectionResult(false, "Your runner could not add the connection — try again, or run `./orkka tracker add` on that machine.")), Json);
            }
        });

        app.MapPost("/internal/runner/tracker-connections/remove", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayRemoveTrackerConnectionRequest>(Json, ct);
            if (req is null || string.IsNullOrWhiteSpace(req.ConnectionId) || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            var (connected, hello) = link.Status;
            if (!connected || hello?.Capabilities?.Contains(RunnerCapabilities.TrackerConnect) is not true)
                return Results.Json(new RelayRemoveTrackerConnectionResponse(false, null), Json);

            try
            {
                var result = await link.InvokeAsync<RemoveTrackerConnectionResult>(
                    RunnerProtocol.RemoveTrackerConnection, [new RemoveTrackerConnectionRequest(req.ConnectionId)], ct);
                return Results.Json(new RelayRemoveTrackerConnectionResponse(true, result, hello.MachineName), Json);
            }
            catch (RunnerUnavailableException)
            {
                return Results.Json(new RelayRemoveTrackerConnectionResponse(false, null), Json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Results.Json(new RelayRemoveTrackerConnectionResponse(true,
                    new RemoveTrackerConnectionResult(false, $"Your runner could not remove the connection: {ex.Message}")), Json);
            }
        });

        app.MapPost("/internal/runner/repo-access", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayRepoAccessRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            var (connected, hello) = link.Status;
            if (!connected
                || hello?.Capabilities?.Contains(RunnerCapabilities.RepoAccessCheck) is not true
                || hello.Capabilities.Contains(RunnerCapabilities.DeveloperGit) is not true)
            {
                return Results.Json(new RelayRepoAccessResponse(false, []), Json);
            }

            // The probe uses exactly what a clone would: the developer's own git credentials.
            var probes = req.Repos.Select(r => new RepoAccessProbe(r.RepoId, r.GitUrl)).ToList();

            try
            {
                var result = await link.InvokeAsync<CheckRepoAccessResponse>(
                    RunnerProtocol.CheckRepoAccess,
                    [new CheckRepoAccessRequest(req.OrgId, [.. probes])],
                    ct);

                return Results.Json(new RelayRepoAccessResponse(
                    true,
                    [.. result.Results.Select(r => new RelayRepoAccessResult(r.RepoId, r.Ok, r.Error))]),
                    Json);
            }
            catch (RunnerUnavailableException)
            {
                // Raced the disconnect — indistinguishable from offline to the caller.
                return Results.Json(new RelayRepoAccessResponse(false, []), Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new RelayRepoAccessResponse(true, [], ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/chat", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            WorkerDrain drain,
            // Explicit: without it a minimal-API parameter of an unregistered service type is
            // read from the request body (see AddAgentsModule).
            [FromServices] ProjectToolRegistry toolRegistry,
            [FromServices] Orkka.Modules.Agents.Contracts.IChatToolServerFactory chatToolServers,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayChatRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            // A chat turn in flight is work a deploy waits for, like a task run.
            using var _ = drain.Track("chat turn");
            var logger = loggerFactory.CreateLogger("Orkka.Worker.Runner.ChatRelay");

            // Everything the turn produces — the agent's own chunks and anything its tools
            // create — leaves through this one channel: tool handlers run concurrently with
            // the stream, and two writers on an HTTP response is a corrupted response.
            var outbound = Channel.CreateUnbounded<ChatChunk>();

            // The turn's platform tools, rebuilt HERE because this pod has the database and
            // the runner link; the runner forwards each call back over CallProjectTool and
            // RunnerChatInvoker registers this server under the run's id for exactly the
            // life of the stream. Emissions become Artifact chunks the API turns back into
            // the same SSE events a cloud turn emits.
            var toolServer = BuildChatToolServer(req, orgId, chatToolServers, outbound, Json, logger);

            var invoker = new RunnerChatInvoker(
                links.GetLink(orgId, req.UserId),
                loggerFactory.CreateLogger<RunnerChatInvoker>(),
                toolRegistry: toolRegistry,
                orgId: orgId,
                readOnlyDirs: req.ReadOnlyDirs);

            // NDJSON stream: one ChatChunk per line, flushed as produced. The offline-
            // runner case surfaces as the invoker's own Error+Done chunk pair.
            http.Response.ContentType = "application/x-ndjson";

            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in invoker.InvokeAgentAsync(
                        req.WorkspaceToken, req.SystemPrompt, req.UserMessage, req.Model,
                        req.ResumeSessionId, conversationId: null, toolServer: toolServer, ct: ct))
                    {
                        await outbound.Writer.WriteAsync(chunk, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "[ChatRelay] Relayed chat turn faulted");
                    outbound.Writer.TryWrite(new ChatChunk(
                        ChatChunkType.Error, $"The chat turn failed on your runner: {ex.Message}"));
                    outbound.Writer.TryWrite(new ChatChunk(ChatChunkType.Done, ""));
                }
                finally
                {
                    outbound.Writer.TryComplete();
                }
            }, CancellationToken.None);

            await foreach (var chunk in outbound.Reader.ReadAllAsync(ct))
            {
                await http.Response.WriteAsync(JsonSerializer.Serialize(chunk, Json), ct);
                await http.Response.WriteAsync("\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            await pump;
            return Results.Empty;
        });

        // ── Editors ──────────────────────────────────────────────────────────
        // Opening a task's worktree in a desktop editor is the escape hatch for taking over
        // from an agent mid-task. Orkka Local does it against its in-process runner link;
        // the hosted API has no link, so it lands here.

        app.MapPost("/internal/runner/editors", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayEditorsRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            var (connected, hello) = link.Status;
            if (!connected || hello?.Capabilities?.Contains(RunnerCapabilities.Editor) is not true)
                return Results.Json(new RelayEditorsResponse(false, []), Json);

            try
            {
                var result = await link.InvokeAsync<ListEditorsResult>(
                    RunnerProtocol.ListEditors, [], ct);
                return Results.Json(new RelayEditorsResponse(
                    true,
                    [.. result.Editors.Select(e => new RelayEditorOption(e.Key, e.DisplayName))]),
                    Json);
            }
            catch (RunnerUnavailableException)
            {
                // Raced the disconnect. Same answer as "offline" — nothing to show.
                return Results.Json(new RelayEditorsResponse(false, []), Json);
            }
        });

        app.MapPost("/internal/runner/open-in-editor", async (
            HttpContext http,
            MultiTenantRunnerLinkProvider links,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayOpenInEditorRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            var link = links.GetLink(orgId, req.UserId);
            var (connected, hello) = link.Status;
            if (!connected)
            {
                return Results.Json(new RelayOpenInEditorResponse(
                    null, "Your runner is not connected — start it with `./orkka start`."),
                    Json);
            }
            if (hello?.Capabilities?.Contains(RunnerCapabilities.Editor) is not true)
            {
                return Results.Json(new RelayOpenInEditorResponse(
                    null,
                    $"Opening an editor needs a newer runner on {hello?.MachineName ?? "your machine"} — " +
                    "update Orkka (`git pull`, then `./orkka start`) and try again."),
                    Json);
            }

            try
            {
                var result = await link.InvokeAsync<RunnerOpResult>(
                    RunnerProtocol.OpenInEditor,
                    [new OpenInEditorRequest(req.WorktreeToken, req.Editor, req.RepoToken)],
                    ct);

                return Results.Json(result.Success
                    ? new RelayOpenInEditorResponse(result.Value ?? "your editor", null)
                    : new RelayOpenInEditorResponse(null, result.Error ?? "Could not open the editor."),
                    Json);
            }
            catch (RunnerUnavailableException ex)
            {
                return Results.Json(new RelayOpenInEditorResponse(null, ex.Message), Json);
            }
        });

        // ── Utility completions ──────────────────────────────────────────────
        // The API host's one-shot utility calls (run-config detection, …) have no runner
        // socket of their own; this is how they reach the user's machine (their
        // subscription, $0). Served=false is a miss — never an error — and the caller takes
        // its runner-offline path; there is no platform-key fallback.
        app.MapPost("/internal/runner/utility", async (
            HttpContext http,
            Orkka.SharedKernel.Ai.IUserRunnerUtility utility,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayUtilityRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId)
                || string.IsNullOrWhiteSpace(req.Model))
                return Results.BadRequest();

            var response = await utility.TryCompleteAsync(orgId, req.UserId,
                new Orkka.SharedKernel.Ai.AnthropicRequest
                {
                    Model = req.Model,
                    MaxTokens = req.MaxTokens > 0 ? req.MaxTokens : 4096,
                    System = req.System,
                    Messages = [new Orkka.SharedKernel.Ai.AnthropicMessage("user", req.UserMessage)],
                }, ct);

            return Results.Json(response is null
                ? new RelayUtilityResponse(false)
                : new RelayUtilityResponse(true, response.Text,
                    response.InputTokens, response.OutputTokens,
                    response.CacheReadTokens, response.CacheWriteTokens), Json);
        });

        MapAppRunRelay(app);
    }

    /// <summary>
    /// App-run relay: the Run button on the hosted app. Commands land here (this pod holds
    /// the user's runner socket and the orchestrator's run state); output flows back to the
    /// browser through WorkerAppRunEvents → TaskHub, not through these responses.
    /// </summary>
    private static void MapAppRunRelay(WebApplication app)
    {
        app.MapPost("/internal/runner/apprun/attach", async (
            HttpContext http,
            Orkka.Runners.Server.MultiTenantRunnerLinkProvider links,
            Orkka.Runners.Server.AppRun.AppRunOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayAppRunAttachRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId)
                || !Ulid.TryParse(req.ProjectId, out var projectId))
                return Results.BadRequest();

            var (connected, hello) = links.GetLink(orgId, req.UserId).Status;
            if (!connected || hello?.Capabilities?.Contains(RunnerCapabilities.AppRun) is not true
                || hello.Capabilities.Contains(RunnerCapabilities.CommandTrust) is not true)
                return Results.Json(new RelayAppRunResponse(false, []), Json);

            await orchestrator.ReconcileAsync(orgId, req.UserId, projectId, ct);
            return Results.Json(new RelayAppRunResponse(
                true, [.. orchestrator.Snapshot(orgId, req.UserId, projectId)]), Json);
        });

        app.MapPost("/internal/runner/apprun/start", async (
            HttpContext http,
            Orkka.Runners.Server.MultiTenantRunnerLinkProvider links,
            Orkka.Runners.Server.AppRun.AppRunOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayAppRunStartRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId)
                || !Ulid.TryParse(req.ProjectId, out var projectId))
                return Results.BadRequest();

            var (connected, hello) = links.GetLink(orgId, req.UserId).Status;
            if (!connected)
            {
                return Results.Json(new RelayAppRunResponse(false, [],
                    "Your runner is not connected — start it with `./orkka start`."), Json);
            }
            // Run targets are shell commands: a runner that cannot vet them gets none.
            if (hello?.Capabilities?.Contains(RunnerCapabilities.AppRun) is not true
                || hello.Capabilities.Contains(RunnerCapabilities.CommandTrust) is not true)
            {
                return Results.Json(new RelayAppRunResponse(false, [],
                    $"Running your project needs a newer runner on {hello?.MachineName ?? "your machine"} — " +
                    "update Orkka (`git pull`, then `./orkka start`) and try again."), Json);
            }

            try
            {
                var planned = req.Targets.Select(t =>
                    new Orkka.Runners.Server.AppRun.PlannedTarget(
                        t.RunId, t.Key, t.Label, t.Kind, t.RepoId, t.Command,
                        t.WorkingDirectory, t.Port, t.Url, t.DependsOn,
                        t.InstallCommand, t.StopCommand, t.PreviewPath, t.RestartOnMerge)).ToList();
                var started = await orchestrator.StartAsync(
                    orgId, req.UserId, projectId, planned, req.ForceFreePorts, ct);
                return Results.Json(new RelayAppRunResponse(true, [.. started]), Json);
            }
            catch (Orkka.Runners.Server.RunnerUnavailableException ex)
            {
                return Results.Json(new RelayAppRunResponse(false, [], ex.Message), Json);
            }
        });

        app.MapPost("/internal/runner/apprun/stop", async (
            HttpContext http,
            Orkka.Runners.Server.AppRun.AppRunOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayAppRunStopRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId)
                || !Ulid.TryParse(req.ProjectId, out var projectId))
                return Results.BadRequest();

            await orchestrator.StopAsync(orgId, req.UserId, projectId, req.RunId, ct);
            return Results.Json(new RelayAppRunResponse(
                true, [.. orchestrator.Snapshot(orgId, req.UserId, projectId)]), Json);
        });

        app.MapPost("/internal/runner/apprun/input", async (
            HttpContext http,
            Orkka.Runners.Server.AppRun.AppRunOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayAppRunInputRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            // Keystrokes execute on someone's machine: the run must belong to the caller
            // the API authenticated, not merely be a runId they learned.
            if (orchestrator.Owns(orgId, req.UserId, req.RunId))
                await orchestrator.InputAsync(req.RunId, req.DataBase64, ct);
            return Results.Ok();
        });

        app.MapPost("/internal/runner/apprun/resize", async (
            HttpContext http,
            Orkka.Runners.Server.AppRun.AppRunOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            if (!IsAuthorized(http, app.Configuration))
                return Results.Unauthorized();

            var req = await http.Request.ReadFromJsonAsync<RelayAppRunResizeRequest>(Json, ct);
            if (req is null || !Ulid.TryParse(req.OrgId, out var orgId))
                return Results.BadRequest();

            if (orchestrator.Owns(orgId, req.UserId, req.RunId))
                await orchestrator.ResizeAsync(req.RunId, req.Cols, req.Rows, ct);
            return Results.Ok();
        });
    }

    private static bool IsAuthorized(HttpContext http, IConfiguration configuration)
        => InternalRelayAuth.IsAuthorized(http, configuration);
}
