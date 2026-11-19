namespace Orkka.LocalRunner.Trackers;

using Orkka.LocalRunner.Contracts;
using Orkka.SharedKernel.Utilities;

/// <summary>
/// Applies what a <c>SyncTrackers</c> answer asks of this machine besides polling: ticket
/// status moves (the only write Orkka makes to a tracker, with the developer's token) and
/// pull-request state lookups (with the developer's forge CLI, so a "Merged" status can
/// follow). The result goes back in one <c>AckTransitions</c>.
/// </summary>
public sealed class TrackerWriteBack(
    TrackerStore store,
    IWorkspaceSecretStore secrets,
    Func<string, ITrackerStatusWriter> writers,
    Func<TrackerPrCheck, CancellationToken, Task<RunnerOpResult>> pullRequestState,
    ILogger<TrackerWriteBack> logger,
    TrackerCredentialResolver? credentials = null)
{
    private readonly TrackerCredentialResolver _credentials = credentials ?? new TrackerCredentialResolver(secrets);

    public static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultPrCheckBudget = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> KnownPrStates = new(StringComparer.Ordinal) { "open", "merged", "closed" };

    /// <summary>The time all pull-request checks of one sync share; a check not finished by
    /// then is left out of the report and the host asks again.</summary>
    internal TimeSpan PrCheckBudget { get; init; } = DefaultPrCheckBudget;

    /// <summary>The report to send, or null when the sync asked for nothing.</summary>
    public async Task<TrackerTransitionReport?> ApplyAsync(TrackerSyncResponse? sync, string instanceId, CancellationToken ct)
    {
        var transitions = sync?.Transitions ?? [];
        var checks = sync?.PrChecks ?? [];
        if (transitions.Length == 0 && checks.Length == 0)
            return null;

        var acks = new List<TrackerTransitionAck>(transitions.Length);
        foreach (var transition in transitions)
            acks.Add(await TransitionAsync(transition, ct));

        var states = await CheckPullRequestsAsync(checks, ct);
        return acks.Count == 0 && states.Count == 0
            ? null
            : new TrackerTransitionReport(instanceId, [.. acks], [.. states]);
    }

    private async Task<TrackerTransitionAck> TransitionAsync(TrackerTransition t, CancellationToken ct)
    {
        TrackerCredential? credential = null;
        try
        {
            var entry = store.Get(t.ConnectionId)
                ?? throw new TrackerException("This machine no longer holds that tracker connection.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TransitionTimeout);
            credential = await _credentials.ResolveAsync(entry, timeout.Token);
            var moved = await writers(entry.Kind).TransitionAsync(credential, t, timeout.Token);
            logger.LogInformation(moved ? "{TicketKey} → {Status}" : "{TicketKey} → {Status} (already there)", t.TicketKey, t.TargetStatus);
            return new TrackerTransitionAck(t.TransitionId, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = ex switch
            {
                TrackerException => ex.Message,
                OperationCanceledException => $"The tracker did not answer within {TransitionTimeout.TotalSeconds:0} seconds.",
                _ => $"The status change failed: {ex.Message}",
            };
            message = credential?.Scrub(message) ?? message;
            logger.LogWarning("{TicketKey} → {Status} failed: {Error}", t.TicketKey, t.TargetStatus, message);
            return new TrackerTransitionAck(t.TransitionId, false, message);
        }
    }

    private async Task<List<TrackerPrState>> CheckPullRequestsAsync(TrackerPrCheck[] checks, CancellationToken ct)
    {
        var states = new List<TrackerPrState>(checks.Length);
        if (checks.Length == 0)
            return states;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(PrCheckBudget);
        foreach (var check in checks)
        {
            if (budget.IsCancellationRequested)
                break;
            try
            {
                var result = await pullRequestState(check, budget.Token);
                if (budget.IsCancellationRequested)
                    break;
                if (!result.Success)
                    states.Add(new TrackerPrState(check.CheckId, null, result.Error ?? "The forge CLI could not read the pull request."));
                else if (result.Value is { } state && KnownPrStates.Contains(state))
                    states.Add(new TrackerPrState(check.CheckId, state));
                else
                    states.Add(new TrackerPrState(check.CheckId, null, $"The forge CLI reported an unexpected state '{result.Value}'."));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Out of budget: omitted, so the host asks again next sync.
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                states.Add(new TrackerPrState(check.CheckId, null, $"Could not read the pull request: {ex.Message}"));
            }
        }
        return states;
    }

    /// <summary>The production lookup: the developer's own forge CLI.</summary>
    public static Func<TrackerPrCheck, CancellationToken, Task<RunnerOpResult>> ViaForgeCli(ForgeCli forgeCli)
        => (check, ct) => Enum.TryParse<ForgeKind>(check.ForgeKind, ignoreCase: true, out var forge)
            ? forgeCli.GetStateAsync(check.RepoUrl, forge, check.CliAccount, check.PullRequestUrl, ct)
            : Task.FromResult(RunnerOpResult.Fail($"Unknown forge '{check.ForgeKind}'."));
}
