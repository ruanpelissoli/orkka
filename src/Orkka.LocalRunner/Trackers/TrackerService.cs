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
    TimeProvider? timeProvider = null)
{
    /// <summary>The Hello goes out every 60s; the trackers do not need to hear from every one.</summary>
    public static readonly TimeSpan HealthRefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _healthGate = new(1, 1);

    public TrackerStore Store => store;
    public IWorkspaceSecretStore Secrets => secrets;

    public ITrackerClient ClientFor(string kind) => kind switch
    {
        TrackerKinds.Jira => jira,
        TrackerKinds.AzureDevOps => ado,
        _ => throw new TrackerException($"Unknown tracker kind '{kind}'."),
    };

    /// <summary>What a status move is written through.</summary>
    public ITrackerStatusWriter WriterFor(string kind) => kind switch
    {
        TrackerKinds.Jira => jira,
        TrackerKinds.AzureDevOps => ado,
        _ => throw new TrackerException($"Unknown tracker kind '{kind}' — update Orkka (`git pull`, then `./orkka start`)."),
    };

    /// <summary>What the poller fetches tickets and comments through.</summary>
    public ITrackerTicketSource SourceFor(string kind) => kind switch
    {
        TrackerKinds.Jira => jira,
        TrackerKinds.AzureDevOps => ado,
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

        if (CredentialFor(entry) is not { } credential)
            return new TrackerQueryResult(false, MissingSecret(entry));

        try
        {
            var client = ClientFor(entry.Kind);
            switch (request.Kind)
            {
                case TrackerQueryKinds.Scopes:
                    return new TrackerQueryResult(true, Scopes: await client.ScopesAsync(credential, ct));
                case TrackerQueryKinds.Epics:
                    return new TrackerQueryResult(true, Epics: await client.EpicsAsync(credential, RequireScope(request), request.Search, ct));
                case TrackerQueryKinds.Statuses:
                    return new TrackerQueryResult(true, Statuses: await client.StatusesAsync(credential, RequireScope(request), ct));
                case TrackerQueryKinds.Boards:
                    return new TrackerQueryResult(true, Boards: await client.BoardsAsync(credential, RequireScope(request), ct));
                case TrackerQueryKinds.Preview:
                    var preview = await client.PreviewAsync(credential, RequireScope(request), request.EpicKeys ?? [],
                        request.ExtraClause, request.IncludeDescendants, request.IntakeStatuses, request.ScopeMode, ct,
                        TrackerBoardRef.From(request.BoardId, request.BoardName));
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

    internal TrackerCredential? CredentialFor(TrackerConnectionEntry entry)
    {
        var secret = secrets.Get(entry.Id);
        return string.IsNullOrEmpty(secret) ? null : new TrackerCredential(entry.SiteUrl, CredentialEmail(entry), secret);
    }

    /// <summary>Jira authenticates as <c>email:token</c>; Azure DevOps as <c>:pat</c>.</summary>
    internal static string? CredentialEmail(TrackerConnectionEntry entry)
        => entry.Kind == TrackerKinds.Jira ? entry.Email : "";

    private async Task<(bool Ok, string? Error, TrackerIdentity? Identity)> TestCredentialAsync(
        TrackerConnectionEntry entry, CancellationToken ct)
    {
        TrackerCredential? credential;
        try
        {
            credential = CredentialFor(entry);
        }
        catch (Exception ex)
        {
            return (false, $"Could not read the token from {secrets.Description}: {ex.Message}", null);
        }
        if (credential is null)
            return (false, MissingSecret(entry), null);
        return await TestAsync(entry.Kind, credential, ct);
    }

    private static string RequireScope(TrackerQueryRequest request)
        => string.IsNullOrWhiteSpace(request.Scope)
            ? throw new TrackerException("Pick a project first.")
            : request.Scope.Trim();

    private string MissingSecret(TrackerConnectionEntry entry) =>
        $"The token for '{entry.Id}' is missing from {secrets.Description} on this machine — run "
        + (entry.Kind == TrackerKinds.Jira
            ? $"`./orkka tracker add jira --site {entry.SiteUrl} --email {entry.Email}`"
            : $"`./orkka tracker add ado --org {entry.SiteUrl}`")
        + " again.";
}
