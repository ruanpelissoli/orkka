namespace Orkka.Modules.Tasks.PublicApi;

using Orkka.Modules.Tasks.Contracts;
using Orkka.Modules.Tasks.Infrastructure.Services;

/// <summary>Board operations — thin delegation to <see cref="BoardOperations"/> so the MCP
/// tools and other modules change task state exactly like the HTTP endpoints do.</summary>
public sealed partial class TasksApi
{
    private BoardOperations Board => board
        ?? throw new InvalidOperationException("BoardOperations is not available in this TasksApi instance.");

    public Task<BoardOpOutcome> ReviewPlanAsync(
        Ulid orgId, Ulid taskId, Ulid planId, bool approve, string? feedback, CancellationToken ct = default)
        => Board.ReviewPlanAsync(orgId, taskId, planId, approve, feedback, ct);

    public Task<PendingPlanSummary?> GetPendingPlanAsync(Ulid orgId, Ulid taskId, CancellationToken ct = default)
        => Board.GetPendingPlanAsync(orgId, taskId, ct);

    public Task<BoardOpOutcome> AnswerTaskAsync(
        Ulid orgId, Ulid taskId, string content, string messageType, string? senderUserId, string? locale,
        CancellationToken ct = default)
        => Board.AnswerTaskAsync(orgId, taskId, content, messageType, senderUserId, locale, ct);

    public Task<BoardOpOutcome> SubmitCodeReviewAsync(
        Ulid orgId, Ulid taskId, string decision, string? body, IReadOnlyList<BoardReviewComment>? comments,
        CancellationToken ct = default)
        => Board.SubmitCodeReviewAsync(orgId, taskId, decision, body, comments, ct);

    public Task<BoardOpOutcome> MergePullRequestAsync(Ulid orgId, Ulid taskId, CancellationToken ct = default)
        => Board.MergePullRequestAsync(orgId, taskId, ct);

    public Task<BoardOpOutcome> SubmitTaskAsync(Ulid orgId, Ulid taskId, string? locale, CancellationToken ct = default)
        => Board.SubmitTaskAsync(orgId, taskId, locale, ct);

    public Task<IReadOnlyList<AttentionTaskSummary>> ListAttentionTasksAsync(
        Ulid orgId, Ulid? projectId, int take, CancellationToken ct = default)
        => Board.ListAttentionTasksAsync(orgId, projectId, take, ct);
}
