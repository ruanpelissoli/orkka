namespace Orkka.Modules.Agents.Features.TaskChat;

public sealed record TaskChatAgentInfo(string Id, string Name, string Role);

public sealed record TaskChatMessageInfo(string Id, string Role, string Content, DateTimeOffset CreatedAt);

/// <summary>A task's Discuss conversation with one agent, plus what the tab needs to render
/// its picker and its lane-dependent "Apply to task" wording.</summary>
public sealed record TaskChatResponse(
    string ConversationId,
    string TaskId,
    string AgentId,
    string AgentName,
    string AgentRole,
    string DefaultAgentId,
    string Lane,
    bool HasActiveRun,
    List<TaskChatMessageInfo> Messages,
    List<TaskChatAgentInfo> Agents);

public sealed record SendTaskChatMessageRequest(string Content, string? AgentId = null);

/// <param name="MessageId">An assistant reply of this task's discussion to apply.</param>
/// <param name="Text">Free text to apply instead (e.g. an edited reply).</param>
/// <param name="Summarize">Apply a utility-model summary of the discussion with
/// <paramref name="AgentId"/>'s agent (default: the task's default agent).</param>
public sealed record ApplyTaskChatRequest(
    string? MessageId = null, string? Text = null, bool Summarize = false, string? AgentId = null);

/// <param name="Action"><c>answered</c> (PendingQuestions — the task resumed),
/// <c>planChangesRequested</c> (PlanReady — the plan was rejected with it and the task re-plans)
/// or <c>posted</c> (a thread message the next run reads).</param>
public sealed record ApplyTaskChatResponse(string Lane, string Status, string Action, string MessageId, string Content);
