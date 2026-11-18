namespace Orkka.Modules.Runners.Features.Editors;

using System.Net.Http.Json;
using System.Text.Json;
using FastEndpoints;
using Microsoft.Extensions.Logging;
using Orkka.LocalRunner.Contracts;
using Orkka.Modules.Runners.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

public sealed record EditorOption(string Key, string DisplayName);

/// <summary>Supported=false means "hide the action" — runner offline, runner too old, task
/// not yours, relay not deployed. The UI does not need to tell those apart.</summary>
public sealed record TaskEditorsResponse(bool Supported, List<EditorOption> Editors);

public sealed record OpenTaskInEditorRequest(string? Editor = null);

public sealed record OpenTaskInEditorResponse(string OpenedWith);

// Wire shapes mirroring the Worker's relay endpoints — duplicated by design, exactly as the
// chat relay records are: the two processes deploy together and the contract is flat JSON.
file sealed record RelayEditorsRequest(string OrgId, string UserId);

file sealed record RelayEditorsResponse(bool Supported, RelayEditorOption[] Editors);

file sealed record RelayEditorOption(string Key, string DisplayName);

file sealed record RelayOpenInEditorRequest(
    string OrgId, string UserId, string WorktreeToken, string? Editor);

file sealed record RelayOpenInEditorResponse(string? OpenedWith, string? Error);

/// <summary>
/// Shared rules for reaching the machine that holds a task's worktree.
///
/// The hard constraint is whose machine it is: a task that runs on someone's runner may only be
/// opened by that someone. A teammate must never be able to launch a process on a colleague's
/// machine, so the runner is resolved from the TASK's RunnerUserId and then checked against
/// the caller.
/// </summary>
internal static class TaskEditorAccess
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null with a reason when there is no machine to open this task on.</summary>
    public static async Task<(TaskSnapshot Task, RunnerRelayTarget Relay)?> ResolveAsync(
        Ulid taskId, ITasksApi tasksApi, ICurrentUser currentUser,
        IRunnerRelayLocator relayLocator, CancellationToken ct)
    {
        var task = await tasksApi.GetTaskSnapshotAsync(taskId, ct);
        if (task is null || task.OrgId != currentUser.OrgId || task.RepoId is null)
            return null;

        if (task.RunnerUserId is null)
            return null;

        // Only the runner's owner. Never take a user id from the request.
        if (!string.Equals(task.RunnerUserId, currentUser.UserId, StringComparison.Ordinal))
            return null;

        var relay = await relayLocator.LocateAsync(task.OrgId, task.RunnerUserId, ct);
        return relay is null ? null : (task, relay);
    }
}

/// <summary>
/// Editors installed on the machine that ran this task. A list rather than a boolean so the
/// UI can offer a picker when someone has both VS Code and Cursor.
/// </summary>
public sealed class ListTaskEditorsEndpoint(
    ITasksApi tasksApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory,
    ILogger<ListTaskEditorsEndpoint> logger)
    : EndpointWithoutRequest<TaskEditorsResponse>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/editors");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));
        var resolved = await TaskEditorAccess.ResolveAsync(
            taskId, tasksApi, currentUser, relayLocator, ct);

        if (resolved is not { } target)
        {
            await Send.OkAsync(new TaskEditorsResponse(false, []), ct);
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
            var response = await client.PostAsJsonAsync(
                $"{target.Relay.BaseUrl}/internal/runner/editors",
                new RelayEditorsRequest(target.Task.OrgId.ToString(), target.Relay.UserId),
                TaskEditorAccess.Json, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RelayEditorsResponse>(
                TaskEditorAccess.Json, ct);

            await Send.OkAsync(new TaskEditorsResponse(
                result?.Supported ?? false,
                [.. (result?.Editors ?? []).Select(e => new EditorOption(e.Key, e.DisplayName))]),
                ct);
        }
        catch (Exception ex)
        {
            // A read that only decides whether to show a button — degrade to "hidden"
            // rather than failing the page around it.
            logger.LogWarning(ex, "Editor list relay failed for task {TaskId}", taskId);
            await Send.OkAsync(new TaskEditorsResponse(false, []), ct);
        }
    }
}

/// <summary>
/// Opens the task's worktree in a desktop editor on the machine that ran it — the escape
/// hatch for taking over from the agent instead of round-tripping another instruction.
/// </summary>
public sealed class OpenTaskInEditorEndpoint(
    ITasksApi tasksApi,
    ICurrentUser currentUser,
    IRunnerRelayLocator relayLocator,
    IHttpClientFactory httpClientFactory)
    : Endpoint<OpenTaskInEditorRequest, OpenTaskInEditorResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/open-in-editor");
    }

    public override async Task HandleAsync(OpenTaskInEditorRequest req, CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));
        var resolved = await TaskEditorAccess.ResolveAsync(
            taskId, tasksApi, currentUser, relayLocator, ct);

        if (resolved is not { } target)
        {
            ThrowError(
                "This task has no workspace on your machine to open — it runs on someone " +
                "else's runner, or your runner is offline.", 422);
            return;
        }

        // The key is validated runner-side against its allowlist; rejecting obvious junk
        // here just produces a better message than "not installed".
        if (req.Editor is { Length: > 0 } requested
            && !RunnerEditors.All.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            ThrowError($"Unknown editor '{requested}'.", 400);
            return;
        }

        var worktreeToken = RunnerTokens.Worktree(
            target.Task.OrgId.ToString(),
            target.Task.RepoId!.Value.ToString(),
            target.Task.Id.ToString());

        var client = httpClientFactory.CreateClient(RunnerRelay.HttpClientName);
        var response = await client.PostAsJsonAsync(
            $"{target.Relay.BaseUrl}/internal/runner/open-in-editor",
            new RelayOpenInEditorRequest(
                target.Task.OrgId.ToString(), target.Relay.UserId, worktreeToken, req.Editor),
            TaskEditorAccess.Json, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RelayOpenInEditorResponse>(
            TaskEditorAccess.Json, ct);

        if (result?.Error is { } error)
        {
            ThrowError(error, 422);
            return;
        }

        await Send.OkAsync(new OpenTaskInEditorResponse(result?.OpenedWith ?? "your editor"), ct);
    }
}
