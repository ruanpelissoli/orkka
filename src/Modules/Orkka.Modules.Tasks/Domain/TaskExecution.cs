namespace Orkka.Modules.Tasks.Domain;

using Orkka.Modules.Tasks.Contracts;
using Orkka.SharedKernel.Domain;


public sealed class TaskExecution : TenantEntity
{
    public Ulid TaskId { get; set; }
    public Ulid? AgentId { get; set; }
    public Lane Lane { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Queued;
    public string? AgentLog { get; set; }
    public string? FilesChanged { get; set; }
    public int TokensUsed { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    // No cost column here on purpose. Cost depends on the per-project markup, admin-managed
    // model rates, and the subscription-auth rule that zeroes a user's own runs — all owned by
    // the Usage module, which already stores it per ExecutionId. Read it through IUsageQuery.
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // CLI session id reported by the run (claude only today) — a later run in the same
    // lane resumes it instead of cold-booting with the full context re-injected. Recorded
    // on failed runs as well as completed ones: a run killed mid-flight is exactly the one
    // whose transcript the next pass should continue rather than replay.
    public string? SessionId { get; set; }

    // Navigation
    public TaskEntity Task { get; set; } = null!;
}
