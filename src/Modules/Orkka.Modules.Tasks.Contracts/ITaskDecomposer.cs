namespace Orkka.Modules.Tasks.Contracts;

/// <param name="DependsOn">Indices of earlier tasks in the same plan that must finish first.
/// Always backwards-pointing, which is what makes a cycle structurally impossible rather than
/// something to detect afterwards.</param>
/// <param name="Repo">Repository name the work belongs in, matched against the project's repos.
/// Null or unmatched falls back to the plan's default repo — a multi-repo project otherwise gets
/// every task filed against one repo, which is how a Go API task lands on the mobile app.</param>
public sealed record DecomposedTask(
    string Title, string Description, string Priority, string Type,
    IReadOnlyList<int>? DependsOn = null,
    string? Repo = null,
    /// <summary>Tasks sharing a key are two halves of ONE piece of work that spans repositories,
    /// and become a cross-repo group on accept. Null — nearly always — is an independent task
    /// that merely happens to live in some repo.</summary>
    int? ContractKey = null);

/// <summary>The interface crossing the boundary between one set of grouped tasks, keyed by
/// <see cref="DecomposedTask.ContractKey"/>.</summary>
public sealed record PlannedContract(int Key, string Contract);

/// <summary>
/// A plan the user has not accepted yet. Nothing is persisted when this is produced — the
/// accept step is a separate call, because auto-creating six tasks that each cost real money on
/// one button press is not a decision a tool should make for someone.
/// </summary>
public sealed record DecompositionProposal(
    string MilestoneTitle,
    string Outcome,
    IReadOnlyList<DecomposedTask> Tasks,
    /// <summary>Contracts for the cross-repo groups in <paramref name="Tasks"/>. Empty for the
    /// ordinary single-repo plan.</summary>
    IReadOnlyList<PlannedContract>? Contracts = null);

/// <param name="Goal">What the user asked for, in their words.</param>
/// <param name="RepoId">Repo whose tree and CLAUDE.md give the model something concrete to plan
/// against. Null falls back to goal-only planning, which is worse but still useful.</param>
/// <param name="MilestoneId">Planning more work into an existing milestone — its outcome is fed
/// to the model and the proposal's own title/outcome are ignored on accept.</param>
public sealed record DecompositionRequest(
    Ulid OrgId,
    Ulid ProjectId,
    string Goal,
    Ulid? RepoId = null,
    Ulid? MilestoneId = null,
    string? Locale = null,
    string? RequesterUserId = null);

public interface ITaskDecomposer
{
    /// <summary>
    /// Plans a goal into a milestone plus tasks, using the project's repo and existing board as
    /// context. Persists nothing.
    /// </summary>
    /// <param name="progress">Reports the phase the run has reached, as a
    /// <see cref="MilestonePlanSteps"/> name. Optional because the call also runs where nobody
    /// is watching; when someone is, this is what they see instead of an indefinite spinner —
    /// and a run that says what it is doing is a run people wait for rather than abandon.</param>
    Task<DecompositionProposal> ProposeAsync(
        DecompositionRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default);


}
