namespace Orkka.Modules.Agents.Features.TaskChat;

using System.Text.Json;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orkka.Modules.Agents.Contracts;
using Orkka.Modules.Agents.Domain;
using Orkka.Modules.Agents.Features.AgentChat;
using Orkka.Modules.Agents.Infrastructure;
using Orkka.Modules.Projects.Contracts;
using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Security;

/// <summary>The task's Discuss conversation with an agent (default: the task's current agent,
/// else the lane's), created on first open.</summary>
public sealed class GetTaskChatEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    TaskChatService chat,
    ChatRunCoordinator coordinator)
    : EndpointWithoutRequest<TaskChatResponse>
{
    public override void Configure()
    {
        Get("/api/tasks/{taskId:ulid}/chat");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var taskId = Ulid.Parse(Route<string>("taskId"));
        var requested = Ulid.TryParse(Query<string?>("agentId", isRequired: false), out var a) ? a : (Ulid?)null;

        var target = await chat.ResolveAsync(currentUser.OrgId, taskId, requested, ct);
        if (target is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var conversation = await chat.GetOrCreateConversationAsync(target, ct);
        var messages = await db.AgentMessages.AsNoTracking()
            .Where(m => m.OrgId == currentUser.OrgId && m.ConversationId == conversation.Id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new TaskChatMessageInfo(m.Id.ToString(), m.Role, m.Content, m.CreatedAt))
            .ToListAsync(ct);

        await Send.OkAsync(new TaskChatResponse(
            conversation.Id.ToString(),
            taskId.ToString(),
            target.Agent.Id.ToString(),
            target.Agent.Name,
            target.Agent.Role.ToString(),
            target.DefaultAgent.Id.ToString(),
            target.Task.CurrentLane.ToString(),
            coordinator.IsRunning(conversation.Id),
            messages,
            [.. target.Agents.Select(x => new TaskChatAgentInfo(x.Id.ToString(), x.Name, x.Role.ToString()))]), ct);
    }
}

/// <summary>
/// One Discuss turn, streamed over SSE with the agent chat's event contract. Reuses the agent
/// chat machinery end to end — <see cref="ChatTurnPreparer"/> (memories, the base folder's
/// sibling repos, workspace, resume), <see cref="ChatRunCoordinator"/>/<c>ChatTurnRunner</c>
/// (detached run, persistence, $0 usage) and the hybrid invoker (the requester's runner) — with
/// two differences: the system prompt carries the task-context block, and the workspace is the
/// task's own read-only checkout of the task branch.
/// </summary>
public sealed class SendTaskChatMessageEndpoint(
    AgentsDbContext db,
    ICurrentUser currentUser,
    TaskChatService chat,
    TaskChatContextBuilder contextBuilder,
    IProjectsApi projectsApi,
    IChatAgentInvoker chatInvoker,
    ChatTurnPreparer preparer,
    ChatRunCoordinator coordinator,
    ILogger<SendTaskChatMessageEndpoint> logger)
    : Endpoint<SendTaskChatMessageRequest>
{
    private const string BusyMessage = "The agent is still working on a reply — wait for it to finish or cancel it first.";

    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/chat");
    }

    public override async Task HandleAsync(SendTaskChatMessageRequest req, CancellationToken ct)
    {
        // The same permission as posting a message on the task thread.
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        if (string.IsNullOrWhiteSpace(req.Content))
        {
            ThrowError("Content is required.");
            return;
        }

        var taskId = Ulid.Parse(Route<string>("taskId"));
        var requested = Ulid.TryParse(req.AgentId, out var a) ? a : (Ulid?)null;
        var target = await chat.ResolveAsync(currentUser.OrgId, taskId, requested, ct);
        if (target is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var conversation = await chat.GetOrCreateConversationAsync(target, ct);
        if (coordinator.IsRunning(conversation.Id))
        {
            ThrowError(BusyMessage, 409);
            return;
        }

        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers.CacheControl = "no-cache";
        HttpContext.Response.Headers.Connection = "keep-alive";

        // Discuss runs only on the requester's own runner — say so before anything is saved or
        // cloned, with the command that fixes it.
        ChatRequesterContext.Set(currentUser.OrgId, currentUser.UserId);
        if (await chatInvoker.GetRouteBlockerAsync(ct) is { } blocker)
        {
            await WriteEventAsync(new { error = blocker, done = true }, ct);
            return;
        }

        var userMessage = new AgentMessage
        {
            OrgId = currentUser.OrgId,
            ConversationId = conversation.Id,
            Role = "user",
            Content = req.Content,
        };
        db.AgentMessages.Add(userMessage);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        ChatTurnSpec spec;
        try
        {
            var task = target.Task;
            var (repo, branches) = TaskChatService.TaskRepo(task, await projectsApi.ListReposAsync(task.ProjectId, ct));
            var contextBlock = await contextBuilder.BuildAsync(task, repo?.Name, ct);

            var preparing = preparer.PrepareAsync(new ChatTurnInput(
                currentUser.OrgId,
                currentUser.UserId,
                currentUser.Locale,
                task.ProjectId,
                target.Agent,
                conversation,
                req.Content,
                ExcludeMessageId: userMessage.Id,
                Task: new TaskChatTurn(task.Id, contextBlock, branches)), ct);

            if (await SendMessageEndpoint.OutlastsAsync(preparing, TimeSpan.FromSeconds(20), ct))
                await WriteEventAsync(new { thinking = "Preparing the task's workspace — a first clone of a large repository can take a few minutes." }, ct);

            spec = await preparing;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Discuss turn preparation failed for task {TaskId}", taskId);
            var reason = $"I couldn't get the task's workspace ready for this turn: {ex.Message}";
            db.AgentMessages.Add(new AgentMessage
            {
                OrgId = currentUser.OrgId,
                ConversationId = conversation.Id,
                Role = "assistant",
                Content = reason,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            await WriteEventAsync(new { error = reason, done = true }, ct);
            return;
        }

        conversation.ActiveRunStartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var handle = coordinator.TryStartRun(spec);
        if (handle is null)
        {
            await WriteEventAsync(new { error = BusyMessage, done = true }, ct);
            return;
        }

        // The run is detached: a closed tab stops this relay, never the reply.
        try
        {
            await foreach (var payload in handle.Events.ReadAllAsync(ct))
                await WriteEventAsync(payload, ct);
        }
        catch (OperationCanceledException)
        {
            handle.MarkDetached();
        }
        catch (Exception ex)
        {
            handle.MarkDetached();
            logger.LogDebug(ex, "SSE relay ended for the Discuss chat of task {TaskId} — run continues detached", taskId);
        }
    }

    private async Task WriteEventAsync(object payload, CancellationToken ct)
    {
        await HttpContext.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await HttpContext.Response.Body.FlushAsync(ct);
    }
}

/// <summary>
/// "Apply to task": a reply (or a summary of the discussion) becomes a human message on the task
/// thread, and in Pending Questions answers them / in Plan Ready requests plan changes. Returns
/// the lane the task is in afterwards.
/// </summary>
public sealed class ApplyTaskChatEndpoint(ICurrentUser currentUser, TaskChatService chat)
    : Endpoint<ApplyTaskChatRequest, ApplyTaskChatResponse>
{
    public override void Configure()
    {
        Post("/api/tasks/{taskId:ulid}/chat/apply");
    }

    public override async Task HandleAsync(ApplyTaskChatRequest req, CancellationToken ct)
    {
        if (!await PermissionGuard.AuthorizeAsync(currentUser, this, Permission.TasksExecute))
            return;

        var taskId = Ulid.Parse(Route<string>("taskId"));
        Ulid? messageId = null;
        if (!string.IsNullOrWhiteSpace(req.MessageId))
        {
            if (!Ulid.TryParse(req.MessageId, out var parsed))
            {
                ThrowError("messageId is not a valid id.");
                return;
            }
            messageId = parsed;
        }

        var requested = Ulid.TryParse(req.AgentId, out var a) ? a : (Ulid?)null;
        var target = await chat.ResolveAsync(currentUser.OrgId, taskId, requested, ct);
        if (target is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        TaskChatApplyResult? result;
        BoardOpOutcome? failure;
        try
        {
            (result, failure) = await chat.ApplyAsync(
                target, messageId, req.Text, req.Summarize, currentUser.UserId, currentUser.Locale, ct);
        }
        catch (TaskChatService.InvalidApplyException ex)
        {
            ThrowError(ex.Message);
            return;
        }
        catch (TaskChatService.SummaryUnavailableException ex)
        {
            ThrowError(ex.Message, 503);
            return;
        }

        if (failure is not null)
        {
            ThrowError(failure.Message ?? "The task could not be updated.", failure.Failure switch
            {
                BoardOpFailure.NotFound => 404,
                BoardOpFailure.Conflict => 409,
                _ => 400,
            });
            return;
        }

        await Send.OkAsync(new ApplyTaskChatResponse(
            result!.Lane.ToString(), result.Status.ToString(), result.Action,
            result.MessageId.ToString(), result.Content), ct);
    }
}
