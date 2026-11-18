namespace Orkka.Modules.Agents.Contracts;

using System.Text;

/// <summary>
/// Turns a failed repo clone into something a human can act on. Shared by agent chat, the
/// grounded briefing and the task pipeline, because it is one failure with one fix and
/// three places it can surface.
///
/// The failure this exists for: every repo is cloned on the developer's machine, by their
/// runner, under their own git credentials — Orkka holds none. Git there runs with terminal
/// prompts disabled, so a machine with no credential helper fails every HTTPS clone of a
/// private repo with <c>could not read Username … terminal prompts disabled</c>, and one with
/// no key the forge knows fails every ssh clone with <c>Permission denied (publickey)</c>.
/// Each caller then correctly declines to work on the repo, and says why.
///
/// Raw git stderr is deliberately not what the user is shown: it describes our subprocess
/// flags, not their problem. Nor is the advice fixed — <see cref="Explain"/> takes whose
/// machine the clone ran on: a clone on the developer's machine is fixed by signing in there,
/// while the (dormant) cloud route has no credential at all.
/// </summary>
public static class RepoAccessDiagnostics
{
    /// <summary>Longest raw error fragment shown for an unclassified failure — enough to
    /// recognise, short enough not to turn the notice into a log dump.</summary>
    private const int MaxDetailLength = 200;

    // Ordered: an authentication failure is matched before "repository not found", because
    // GitHub answers a private repo the caller cannot see with the latter. Guessing
    // "deleted" when the truth is "not signed in" sends the user to fix the wrong thing.
    private static readonly string[] CredentialMarkers =
    [
        "could not read username",
        "could not read password",
        "terminal prompts disabled",
        "authentication failed",
        "invalid username or password",
        "permission denied (publickey)",
        "support for password authentication was removed",
        "no anonymous write access",
        "403 forbidden",
        "http basic: access denied",
    ];

    private static readonly string[] NotFoundMarkers =
    [
        "repository not found",
        "remote: not found",
        "does not appear to be a git repository",
        "could not read from remote repository",
    ];

    private static readonly string[] RunnerUnavailableMarkers =
    [
        "not connected",
        "no runner",
        "runner is offline",
        "runner disconnected",
    ];

    /// <summary>Classifies a clone/fetch failure from the error text the runner reported.</summary>
    public static RepoAccessFailureReason Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return RepoAccessFailureReason.Unknown;

        var text = error.ToLowerInvariant();

        if (Array.Exists(CredentialMarkers, text.Contains))
            return RepoAccessFailureReason.GitCredentials;
        if (Array.Exists(NotFoundMarkers, text.Contains))
            return RepoAccessFailureReason.RepoNotFound;
        if (Array.Exists(RunnerUnavailableMarkers, text.Contains))
            return RepoAccessFailureReason.RunnerUnavailable;

        return RepoAccessFailureReason.Unknown;
    }

    /// <summary>
    /// True when the reason cannot change without the user doing something — retrying the
    /// identical clone next turn is guaranteed to fail the same way. Drives how long the
    /// failure is worth caching: a permanent one is worth a real backoff window, an
    /// unclassified or transport failure only a brief one, because the runner coming back
    /// or a network blip clearing must take effect right away.
    /// </summary>
    public static bool IsPermanent(RepoAccessFailureReason reason) => reason
        is RepoAccessFailureReason.GitCredentials
        or RepoAccessFailureReason.RepoNotFound;

    /// <summary>
    /// The notice for one chat turn, or null when every repo made it in. Names the repos —
    /// "some repos are missing" is not actionable — and says what to do about the reason.
    /// </summary>
    public static string? BuildNotice(IReadOnlyList<UnavailableChatRepo> unavailable)
    {
        if (unavailable.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append(unavailable.Count == 1
            ? "One repo is missing from this conversation, so I can't read its code:"
            : $"{unavailable.Count} repos are missing from this conversation, so I can't read their code:");

        // Grouped by reason AND by whose machine failed: two repos that both "could not
        // authenticate" need opposite advice if one ran on the user's runner and the other in
        // the cloud, so they must not share a bullet.
        foreach (var group in unavailable.GroupBy(r => (r.Reason, r.OnUserMachine)))
        {
            var names = string.Join(", ", group.Select(r => r.Name));
            sb.Append("\n\n• ").Append(names).Append(" — ")
              .Append(Explain(group.Key.Reason, group.Key.OnUserMachine));

            // Only the unclassified case earns the raw text: for the others the explanation
            // above already says more than the stderr does.
            if (group.Key.Reason is RepoAccessFailureReason.Unknown
                && group.Select(r => r.Detail).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) is { } detail)
                sb.Append(" (").Append(Truncate(detail)).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>
    /// One sentence of advice. <paramref name="onUserMachine"/> decides whose git failed and
    /// therefore who can fix it — see <see cref="UnavailableChatRepo"/>. Public because the
    /// task pipeline explains the same failures in its own thread.
    /// </summary>
    public static string Explain(RepoAccessFailureReason reason, bool onUserMachine) => reason switch
    {
        // Names the machine, the cause and the fix. "Within a few minutes" is not padding:
        // a failed clone is remembered for a short window (BareCloneFailureCache) so a
        // broken repo is not retried on every single turn, and promising that the very next
        // message picks the fix up would be a promise this makes false.
        // The runner clones with the developer's own git credentials, so the fix is on that
        // machine: sign git in there the way they would for any clone.
        RepoAccessFailureReason.GitCredentials when onUserMachine =>
            "the repository refused your git credentials when your runner tried to clone it. "
            + "Sign git in on that machine — your credential manager or `gh auth login` for an "
            + "https remote, an ssh key the forge knows for an ssh one — and the next few minutes "
            + "will pick the fix up.",

        // Orkka holds no forge credential, so a clone anywhere but the developer's machine
        // has nothing to authenticate with. Nothing they sign into fixes that; their runner does.
        RepoAccessFailureReason.GitCredentials =>
            "Orkka has no credential for this repository — repository work runs on your own "
            + "machine. Start Orkka there (`./orkka start`, which runs the runner) and the work moves there.",

        RepoAccessFailureReason.RepoNotFound =>
            "the remote reports no such repository. Check the repo's URL in project settings — it may "
            + "have been renamed, moved, or deleted.",

        RepoAccessFailureReason.NotCloned =>
            "it hasn't finished cloning yet. Try again in a moment.",

        RepoAccessFailureReason.RunnerUnavailable =>
            "your Orkka runner wasn't reachable, so the code on your machine couldn't be opened. "
            + "Check that it's running and connected.",

        _ => "it couldn't be opened on the machine running this work.",
    };

    /// <summary>
    /// The same failure worded for a task run rather than a chat turn — one line, because it
    /// lands in the task thread next to the phase that was starting. Task runs clone with the
    /// developer's git credentials exactly as chat does, so a machine without them fails their
    /// tasks too; until this it said so in raw git stderr.
    /// </summary>
    public static string DescribeCloneFailure(string repoName, string? error, bool onUserMachine)
    {
        var reason = Classify(error);
        var message = $"Could not open {repoName}: {Explain(reason, onUserMachine)}";

        return reason is RepoAccessFailureReason.Unknown && !string.IsNullOrWhiteSpace(error)
            ? $"{message} ({Truncate(error)})"
            : message;
    }

    private static string Truncate(string detail)
    {
        // Git puts the useful line first and its own advice after; one line is the signal.
        var line = detail.ReplaceLineEndings("\n").Split('\n')
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? detail.Trim();

        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength] + "…";
    }
}
