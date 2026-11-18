namespace Orkka.Modules.Tasks.Contracts;

/// <summary>
/// Classifies merge failure reasons that no resolution strategy can fix — the user's
/// environment must change first (Orkka Local squash-merge preconditions, runner
/// connectivity). The merge page shows these as a "fix this first" screen instead of
/// the conflict chooser, and the orchestrator skips the resolver agent for them.
/// Matched against the user-facing failure text because the messages cross two
/// process boundaries (runner → host → DB) as plain strings.
/// </summary>
public static class MergeBlockedReasons
{
    private static readonly string[] Patterns =
    [
        // RunnerOperations.SquashMergeIntoUserRepoAsync preconditions
        "uncommitted changes",
        "check out '",
        "is not in your repository",
        "repository path no longer exists",
        "cannot inspect the repository",
        // Runner link (RunnerNotConnected.Message / RunnerLinkMessages)
        "runner is not connected",
        "runner disconnected",
    ];

    public static bool IsEnvironmentBlocked(string? reason)
        => !string.IsNullOrEmpty(reason)
           && Patterns.Any(p => reason.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The one environment block the platform can clear on its own: the task's
    /// HEAD branch is missing from the user's repository (the runner's wording for a push
    /// that never happened), as opposed to the base branch being missing.</summary>
    public static bool IsMissingHeadBranch(string? reason)
        => !string.IsNullOrEmpty(reason)
           && reason.Contains("is not in your repository", StringComparison.OrdinalIgnoreCase)
           && reason.Contains("may not have pushed", StringComparison.OrdinalIgnoreCase);
}
