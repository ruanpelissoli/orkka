namespace Orkka.Runners.Server;

/// <summary>
/// Per-operation ceilings for runner ops that legitimately outrun <c>RunnerLink</c>'s default
/// five minutes. The default is right for a fetch, a worktree, a push; it is wrong for the
/// FIRST clone of a repository on a machine that has never seen it — a large repo with many
/// branches over a home connection took the whole five minutes on 2026-09-07 and the chat
/// turn that needed it died with "Invocation canceled by the server" while the clone went on
/// to finish seconds later. A clone that is still running is progress, not a hang; the
/// runner-side git process has its own failure modes and reports them.
/// </summary>
public static class RunnerOpBudgets
{
    /// <summary>EnsureBareClone: first clone included. Thirty minutes covers a multi-GB
    /// monorepo on a slow link; a repo that needs more is a repo to talk about.</summary>
    public static readonly TimeSpan Clone = TimeSpan.FromMinutes(30);

    /// <summary>The budget for a runner op by method name, or null for the link default.</summary>
    public static TimeSpan? For(string method) =>
        method == Orkka.LocalRunner.Contracts.RunnerProtocol.EnsureBareClone ? Clone : null;
}
