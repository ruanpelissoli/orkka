namespace Orkka.LocalRunner.Trackers;

using Orkka.LocalRunner.Contracts;

/// <summary>
/// The runner's side of tracker connections: answers the host's <c>TrackerQuery</c> op and
/// reports connection metadata in the Hello. The token is read from the secret store for
/// each call and never leaves this class except as an Authorization header to the tracker —
/// every error that goes back to the host or a terminal is scrubbed of it first.
/// </summary>
public sealed class TrackerService(
    TrackerStore store,
    IWorkspaceSecretStore secrets,
    JiraCloudClient jira,
    AzureDevOpsClient ado,
    ILogger<TrackerService> logger,
    TimeProvider? timeProvider = null,
    GitHubProjectsClient? github = null,
    TrackerCredentialResolver? credentials = null)
{
    /// <summary>The Hello goes out every 60s; the trackers do not need to hear from every one.</summary>
    public static readonly TimeSpan HealthRefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _healthGate = new(1, 1);
    private readonly GitHubProjectsClient _github = github ?? new GitHubProjectsClient(TrackerHttp.CreateDefault());

    public TrackerStore Store => store;
    public IWorkspaceSecretStore Secrets => secrets;

    /// <summary>Where every call's credential comes from: the secret store, or the gh login.</summary>
    public TrackerCredentialResolver Credentials { get; } = credentials ?? new TrackerCredentialResolver(secrets);

    public ITrackerClient ClientFor(string kind) => kind switch
    {
        TrackerKinds.Jira => jira,
        TrackerKinds.AzureDevOps => ado,
        TrackerKinds.GitHub => _github,
        _ => throw new TrackerException($"Unknown tracker kind '{kind}'."),
    };

    /// <summary>What a status move is written through.</summary>
    public ITrackerStatusWriter WriterFor(string kind) => kind switch
    {
        TrackerKinds.Jira => jira,
        TrackerKinds.AzureDevOps => ado,
        TrackerKinds.GitHub => _github,
        _ => throw new TrackerException($"Unknown tracker kind '{kind}' — update Orkka (`git pull`, then `./orkka start`)."),
    };

    /// <summary>What the poller fetches tickets and comments through.</summary>
    public ITrackerTicketSource SourceFor(string kind) => kind switch
    {
        TrackerKinds.Jira => jira,
        TrackerKinds.AzureDevOps => ado,
        TrackerKinds.GitHub => _github,
        _ => throw new TrackerException($"Unknown tracker kind '{kind}' — update Orkka (`git pull`, then `./orkka start`)."),
    };

    /// <summary>The Hello's tracker list. Health is re-checked for entries older than
    /// <see cref="HealthRefreshInterval"/>; a failing check marks the entry unhealthy. Never
    /// throws — a broken trackers.json must not keep the runner from connecting.</summary>
    public async Task<RunnerTrackerConnection[]> ListConnectionsAsync(CancellationToken ct)
    {
        try
        {
            await _healthGate.WaitAsync(ct);
            try
            {
                var now = _time.GetUtcNow();
                var stale = store.List()
                    .Where(e => e.CheckedAt is not { } at || now - at >= HealthRefreshInterval)
                    .ToList();
                await Task.WhenAll(stale.Select(async entry =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(HealthCheckTimeout);
                    try
                    {
                        await CheckAsync(entry, timeout.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        store.Update(entry.Id, e => e with
                        {
                            Healthy = false,
                            HealthDetail = $"{e.SiteUrl} did not answer within {HealthCheckTimeout.TotalSeconds:0} seconds.",
                            CheckedAt = _time.GetUtcNow(),
                        });
                    }
                }));
            }
            finally
            {
                _healthGate.Release();
            }
            return [.. store.List().Select(e => e.ToMetadata())];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read the tracker connections ({Error}) — reporting none.", ex.Message);
            return [];
        }
    }

    /// <summary>Tests the stored token now and records the outcome on the entry.</summary>
    public async Task<TrackerConnectionEntry> CheckAsync(TrackerConnectionEntry entry, CancellationToken ct)
    {
        var (ok, error, identity) = await TestCredentialAsync(entry, ct);
        var updated = entry with
        {
            Healthy = ok,
            HealthDetail = ok ? null : error,
            CheckedAt = _time.GetUtcNow(),
            AccountId = identity?.AccountId ?? entry.AccountId,
            DisplayName = identity?.DisplayName ?? entry.DisplayName,
        };
        store.Update(entry.Id, _ => updated);
        return updated;
    }

    public async Task<TrackerQueryResult> QueryAsync(TrackerQueryRequest request, CancellationToken ct)
    {
        var entry = store.Get(request.ConnectionId ?? "");
        if (entry is null)
        {
            return new TrackerQueryResult(false,
                $"This machine has no tracker connection '{request.ConnectionId}'. Run `./orkka tracker list` on it to see the ones it has.");
        }

        if (request.Kind == TrackerQueryKinds.Test)
        {
            var checkedEntry = await CheckAsync(entry, ct);
            return new TrackerQueryResult(checkedEntry.Healthy, checkedEntry.HealthDetail, Connection: checkedEntry.ToMetadata());
        }

        if (request.Kind == TrackerQueryKinds.DryRun)
            return await DryRunAsync(request, ct);

        TrackerCredential credential;
        try
        {
            credential = await Credentials.ResolveAsync(entry, ct);
        }
        catch (TrackerException ex)
        {
            return new TrackerQueryResult(false, ex.Message);
        }

        try
        {
            var client = ClientFor(entry.Kind);
            var board = TrackerBoardRef.From(request.BoardId, request.BoardName);
            switch (request.Kind)
            {
                case TrackerQueryKinds.Scopes:
                    return new TrackerQueryResult(true, Scopes: await client.ScopesAsync(credential, ct));
                case TrackerQueryKinds.Epics:
                    return new TrackerQueryResult(true, Epics: await client.EpicsAsync(credential, RequireScope(request), request.Search, ct));
                case TrackerQueryKinds.Statuses:
                    return new TrackerQueryResult(true, Statuses: await client.StatusesAsync(credential, RequireScope(request), ct,
                        board, request.StatusField));
                case TrackerQueryKinds.Fields:
                    return new TrackerQueryResult(true, Fields: await GitHubOnly(entry).FieldsAsync(credential, request.Scope ?? "", board, ct));
                case TrackerQueryKinds.Repos:
                    return new TrackerQueryResult(true, Repositories: await GitHubOnly(entry).RepositoriesAsync(credential, request.Scope ?? "", board, ct));
                case TrackerQueryKinds.Boards:
                    return new TrackerQueryResult(true, Boards: await client.BoardsAsync(credential, RequireScope(request), ct));
                case TrackerQueryKinds.Preview:
                    var preview = await client.PreviewAsync(credential, RequireScope(request), request.EpicKeys ?? [],
                        request.ExtraClause, request.IncludeDescendants, request.IntakeStatuses, request.ScopeMode, ct,
                        board, request.StatusField, request.RepoFilter);
                    return new TrackerQueryResult(true, MatchCount: preview.MatchCount, Sample: preview.Sample,
                        EffectiveQuery: preview.EffectiveQuery);
                default:
                    return new TrackerQueryResult(false, $"This runner does not know the tracker query '{request.Kind}' — update Orkka (`git pull`, then `./orkka start`).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var error = credential.Scrub(ex is TrackerException ? ex.Message : $"The tracker query failed: {ex.Message}");
            logger.LogDebug("Tracker query {Kind} on {ConnectionId} failed: {Error}", request.Kind, entry.Id, error);
            return new TrackerQueryResult(false, error);
        }
    }

    /// <summary>What the real poll uses — set by the runner once the poller exists (it reads
    /// tickets through <see cref="SourceFor"/>). Null in a runner built without polling.</summary>
    public TrackerTicketPoller? Poller { get; set; }

    /// <summary>
    /// The setup page's dry run: the assignment the host would hand this machine, polled by the
    /// real poller with <c>dryRun</c> on — the same reads, the same normalization, nothing cached
    /// or remembered — and the tickets returned instead of reported.
    /// </summary>
    private async Task<TrackerQueryResult> DryRunAsync(TrackerQueryRequest request, CancellationToken ct)
    {
        if (request.Assignment is not { } assignment)
            return new TrackerQueryResult(false, "The dry run needs the ticket source to read — save it first.");
        if (!string.Equals(assignment.ConnectionId, request.ConnectionId, StringComparison.Ordinal))
            return new TrackerQueryResult(false, "The dry run's ticket source uses another connection.");
        if (Poller is null)
            return new TrackerQueryResult(false, "This runner cannot read tickets — update Orkka (`git pull`, then `./orkka start`).");

        var outcome = await Poller.PollAsync(assignment, "dry-run", ct, dryRun: true);
        return outcome.Report.Ok
            ? new TrackerQueryResult(true, Tickets: outcome.Report.Tickets, MatchCount: outcome.Report.Tickets.Count(t => t.InIntake))
            : new TrackerQueryResult(false, outcome.Report.Error);
    }

    /// <summary>Tests a credential without touching the store — what `tracker add` runs
    /// before anything is saved. The error is scrubbed.</summary>
    public async Task<(bool Ok, string? Error, TrackerIdentity? Identity)> TestAsync(
        string kind, TrackerCredential credential, CancellationToken ct)
    {
        try
        {
            return (true, null, await ClientFor(kind).TestAsync(credential, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = ex switch
            {
                TrackerException => ex.Message,
                OperationCanceledException => "The tracker did not answer in time.",
                _ => $"The check failed: {ex.Message}",
            };
            return (false, credential.Scrub(message), null);
        }
    }

    /// <summary>Jira authenticates as <c>email:token</c>; Azure DevOps as <c>:pat</c>.</summary>
    internal static string? CredentialEmail(TrackerConnectionEntry entry) => TrackerCredentialResolver.Email(entry);

    private GitHubProjectsClient GitHubOnly(TrackerConnectionEntry entry)
        => entry.Kind == TrackerKinds.GitHub
            ? _github
            : throw new TrackerException("That question is only for GitHub Projects connections.");

    private async Task<(bool Ok, string? Error, TrackerIdentity? Identity)> TestCredentialAsync(
        TrackerConnectionEntry entry, CancellationToken ct)
    {
        TrackerCredential credential;
        try
        {
            credential = await Credentials.ResolveAsync(entry, ct);
        }
        catch (TrackerException ex)
        {
            return (false, ex.Message, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"Could not read the token from {secrets.Description}: {ex.Message}", null);
        }
        return await TestAsync(entry.Kind, credential, ct);
    }

    private static string RequireScope(TrackerQueryRequest request)
        => string.IsNullOrWhiteSpace(request.Scope)
            ? throw new TrackerException("Pick a project first.")
            : request.Scope.Trim();

}
