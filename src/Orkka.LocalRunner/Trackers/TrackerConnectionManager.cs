namespace Orkka.LocalRunner.Trackers;

using Orkka.LocalRunner.Contracts;

/// <summary>
/// Adding and removing a tracker connection — one implementation for `./orkka tracker
/// add|remove` and for the settings page (<c>AddTrackerConnection</c> / <c>RemoveTrackerConnection</c>,
/// relayed over loopback from the developer's own host). The token is tested against the
/// tracker first; only a working one is stored, in the OS secret store under the connection's
/// id, and only metadata goes into trackers.json and back to the caller. Every error is scrubbed
/// of the token, and nothing here logs a request. A GitHub connection may instead use the
/// developer's gh login: then nothing is stored at all — the token is read from gh at every call.
/// </summary>
public sealed class TrackerConnectionManager(TrackerService service, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(45);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public TrackerService Service => service;

    /// <summary>"jira", "ado", "github" and their spellings onto a <see cref="TrackerKinds"/> value.</summary>
    public static string? NormalizeKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "jira" => TrackerKinds.Jira,
        "ado" or "azure-devops" or "azuredevops" or "azure" => TrackerKinds.AzureDevOps,
        "github" or "gh" or "github-projects" => TrackerKinds.GitHub,
        _ => null,
    };

    public const string GitHubServerNotSupported =
        "Give github.com or your GHE.com host (like acme.ghe.com) — GitHub Enterprise Server is not supported.";

    /// <summary>Tests and stores. A failed test stores nothing.</summary>
    public async Task<AddTrackerConnectionResult> AddAsync(AddTrackerConnectionRequest request, CancellationToken ct)
    {
        var kind = NormalizeKind(request.Kind);
        if (kind is null)
            return new AddTrackerConnectionResult(false, "Pick Jira Cloud, Azure DevOps or GitHub.");
        if (kind == TrackerKinds.GitHub)
            return await AddGitHubAsync(request, ct);

        string siteUrl;
        string? email = null;
        if (kind == TrackerKinds.Jira)
        {
            if (TrackerSites.NormalizeJiraSite(request.Site) is not { } site)
                return new AddTrackerConnectionResult(false, "Give your Jira Cloud site, like acme.atlassian.net.");
            email = request.Email?.Trim();
            if (string.IsNullOrEmpty(email) || !email.Contains('@'))
                return new AddTrackerConnectionResult(false,
                    "Give the email of your Atlassian account — Jira API tokens authenticate as email + token.");
            siteUrl = site;
        }
        else
        {
            if (TrackerSites.NormalizeAdoOrg(request.Site) is not { } org)
                return new AddTrackerConnectionResult(false, "Give your Azure DevOps organization, like https://dev.azure.com/acme (or just acme).");
            siteUrl = org;
        }

        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token))
            return new AddTrackerConnectionResult(false, "No token given — nothing was saved.");

        var credential = new TrackerCredential(siteUrl, kind == TrackerKinds.Jira ? email : "", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        var (ok, error, identity) = await service.TestAsync(kind, credential, timeout.Token);
        if (!ok)
            return new AddTrackerConnectionResult(false, $"{credential.Scrub(error)} Nothing was saved.");

        var existing = service.Store.FindSame(kind, siteUrl, email);
        var id = existing?.Id ?? service.Store.NextId(kind, siteUrl);
        try
        {
            service.Secrets.Set(id, token);
        }
        catch (Exception ex)
        {
            return new AddTrackerConnectionResult(false,
                $"Could not store the token in {service.Secrets.Description}: {credential.Scrub(ex.Message)} Nothing was saved.");
        }

        var now = _time.GetUtcNow();
        var entry = new TrackerConnectionEntry
        {
            Id = id,
            Kind = kind,
            SiteUrl = siteUrl,
            Email = email,
            AccountId = identity?.AccountId,
            DisplayName = identity?.DisplayName,
            Healthy = true,
            CheckedAt = now,
            AddedAt = existing?.AddedAt ?? now,
        };
        service.Store.Upsert(entry);
        return new AddTrackerConnectionResult(true, null, entry.ToMetadata());
    }

    /// <summary>GitHub: the gh login (default — nothing stored) or a token (stored like the others).</summary>
    private async Task<AddTrackerConnectionResult> AddGitHubAsync(AddTrackerConnectionRequest request, CancellationToken ct)
    {
        if (TrackerSites.NormalizeGitHubHost(request.Site) is not { } siteUrl)
            return new AddTrackerConnectionResult(false, GitHubServerNotSupported);
        var useGh = request.AuthMode is null || TrackerAuthModes.IsGhCli(request.AuthMode);
        if (!useGh && !string.Equals(request.AuthMode, TrackerAuthModes.Token, StringComparison.OrdinalIgnoreCase))
            return new AddTrackerConnectionResult(false, "Use your gh login (gh) or a token (token).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);
        TrackerCredential credential;
        string? token = null;
        if (useGh)
        {
            try
            {
                credential = await service.Credentials.FromGhAsync(siteUrl, timeout.Token);
            }
            catch (TrackerException ex)
            {
                return new AddTrackerConnectionResult(false, $"{ex.Message} Nothing was saved.");
            }
        }
        else
        {
            token = request.Token?.Trim();
            if (string.IsNullOrEmpty(token))
                return new AddTrackerConnectionResult(false, "No token given — nothing was saved.");
            credential = new TrackerCredential(siteUrl, "", token, bearer: true);
        }

        var (ok, error, identity) = await service.TestAsync(TrackerKinds.GitHub, credential, timeout.Token);
        if (!ok)
            return new AddTrackerConnectionResult(false, $"{credential.Scrub(error)} Nothing was saved.");

        var existing = service.Store.FindSame(TrackerKinds.GitHub, siteUrl, null);
        var id = existing?.Id ?? service.Store.NextId(TrackerKinds.GitHub, siteUrl);
        try
        {
            if (token is not null)
                service.Secrets.Set(id, token);
            else if (service.Secrets.Get(id) is not null)
                service.Secrets.Delete(id); // switched to the gh login: no token left behind
        }
        catch (Exception ex)
        {
            return new AddTrackerConnectionResult(false,
                $"Could not update {service.Secrets.Description}: {credential.Scrub(ex.Message)} Nothing was saved.");
        }

        var now = _time.GetUtcNow();
        var entry = new TrackerConnectionEntry
        {
            Id = id,
            Kind = TrackerKinds.GitHub,
            SiteUrl = siteUrl,
            Auth = useGh ? TrackerAuthModes.GhCli : null,
            AccountId = identity?.AccountId,
            DisplayName = identity?.DisplayName,
            Healthy = true,
            CheckedAt = now,
            AddedAt = existing?.AddedAt ?? now,
        };
        service.Store.Upsert(entry);
        return new AddTrackerConnectionResult(true, null, entry.ToMetadata());
    }

    /// <summary>Deletes the token, then the entry — a token whose entry is gone could never be
    /// found to delete again.</summary>
    public RemoveTrackerConnectionResult Remove(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || service.Store.Get(id) is not { } entry)
            return new RemoveTrackerConnectionResult(false, $"No tracker connection '{id}' on this machine.");
        try
        {
            if (!entry.UsesGhCli || service.Secrets.Get(entry.Id) is not null)
                service.Secrets.Delete(entry.Id); // a gh-login connection has no token here; gh keeps its own
        }
        catch (Exception ex)
        {
            return new RemoveTrackerConnectionResult(false, $"Could not delete the token from {service.Secrets.Description}: {ex.Message}");
        }
        service.Store.Remove(entry.Id);
        return new RemoveTrackerConnectionResult(true);
    }
}
