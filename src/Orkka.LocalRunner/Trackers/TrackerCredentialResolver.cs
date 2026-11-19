namespace Orkka.LocalRunner.Trackers;

using Orkka.LocalRunner.Contracts;

/// <summary>
/// What a tracker call authenticates with, for one connection: the token in the OS secret store,
/// or — for a GitHub connection that uses the developer's gh login — the token gh holds for the
/// host, read with <c>gh auth token --hostname {host}</c> at call time and kept nowhere. The one
/// place the poller, the write-back, the settings queries and the health check get it from.
/// </summary>
public sealed class TrackerCredentialResolver(
    IWorkspaceSecretStore secrets,
    Func<string, CancellationToken, Task<GhCliCredentials.TokenResult>>? ghToken = null)
{
    private readonly Func<string, CancellationToken, Task<GhCliCredentials.TokenResult>> _ghToken
        = ghToken ?? ((host, ct) => GhCliCredentials.TokenAsync(host, ct));

    public string Description => secrets.Description;

    /// <summary>The credential, or a <see cref="TrackerException"/> saying what to do.</summary>
    public async Task<TrackerCredential> ResolveAsync(TrackerConnectionEntry entry, CancellationToken ct)
    {
        if (entry.UsesGhCli)
            return await FromGhAsync(entry.SiteUrl, ct);

        var secret = secrets.Get(entry.Id);
        if (string.IsNullOrEmpty(secret))
            throw new TrackerException(MissingSecret(entry));
        return new TrackerCredential(entry.SiteUrl, Email(entry), secret, bearer: entry.Kind == TrackerKinds.GitHub);
    }

    /// <summary>The developer's gh login for a GitHub site.</summary>
    public async Task<TrackerCredential> FromGhAsync(string siteUrl, CancellationToken ct)
    {
        var host = TrackerSites.GitHubCliHost(siteUrl);
        var result = await _ghToken(host, ct);
        if (!result.Ok)
            throw new TrackerException(result.Error ?? $"The GitHub CLI has no login for {host}.");
        return new TrackerCredential(siteUrl, "", result.Token!, bearer: true) { GhCliHost = host };
    }

    /// <summary>Jira authenticates as <c>email:token</c>; Azure DevOps as <c>:pat</c>; GitHub
    /// as a bearer token.</summary>
    internal static string? Email(TrackerConnectionEntry entry)
        => entry.Kind == TrackerKinds.Jira ? entry.Email : "";

    private string MissingSecret(TrackerConnectionEntry entry) =>
        $"The token for '{entry.Id}' is missing from {secrets.Description} on this machine — run "
        + entry.Kind switch
        {
            TrackerKinds.Jira => $"`./orkka tracker add jira --site {entry.SiteUrl} --email {entry.Email}`",
            TrackerKinds.GitHub => $"`./orkka tracker add github --host {TrackerSites.GitHubCliHost(entry.SiteUrl)} --token`",
            _ => $"`./orkka tracker add ado --org {entry.SiteUrl}`",
        }
        + " again.";
}
