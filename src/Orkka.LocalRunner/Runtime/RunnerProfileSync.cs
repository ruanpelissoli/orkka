namespace Orkka.LocalRunner.Runtime;

using System.Text;
using Orkka.LocalRunner.Cli;
using Orkka.LocalRunner.Trackers;

/// <summary>
/// Turns the local host's bootstrap answer into runner workspace profiles: a workspace whose
/// token was just minted gets a profile (found again by its <see cref="WorkspaceProfile.WorkspaceId"/>,
/// else named after the workspace), and every existing profile of a known workspace is pointed
/// at the host's current port. The running runner picks new profiles up by itself
/// (<see cref="RunnerSupervisor"/> hot-add).
/// </summary>
public static class RunnerProfileSync
{
    public sealed record Outcome(IReadOnlyList<string> Added, IReadOnlyList<string> Updated, IReadOnlyList<string> MissingToken);

    public static Outcome Apply(
        string orkkaRoot,
        string hostUrl,
        IReadOnlyList<BootstrapWorkspace> workspaces,
        Func<string, string, IWorkspaceSecretStore>? secretStores = null)
    {
        secretStores ??= WorkspaceSecretStores.CreateDefault;
        var added = new List<string>();
        var updated = new List<string>();
        var missing = new List<string>();
        var profiles = WorkspaceProfiles.List(orkkaRoot).ToList();

        foreach (var workspace in workspaces)
        {
            var existing = profiles.FirstOrDefault(p => p.WorkspaceId == workspace.WorkspaceId);
            if (workspace.Token is { Length: > 0 } token)
            {
                var name = existing?.Name ?? UniqueName(workspace, profiles);
                var profile = new WorkspaceProfile
                {
                    Name = name,
                    HostUrl = hostUrl,
                    Token = token,
                    GitAuthorName = existing?.GitAuthorName,
                    GitAuthorEmail = existing?.GitAuthorEmail,
                    WorkspaceId = workspace.WorkspaceId,
                };
                WorkspaceProfiles.Save(orkkaRoot, profile, secretStores(orkkaRoot, name));
                profiles.RemoveAll(p => p.Name == name);
                profiles.Add(profile);
                (existing is null ? added : updated).Add(name);
            }
            else if (existing is null)
            {
                // The host minted this workspace's token for a profile this machine no longer
                // has; the runner cannot serve it until the host mints a new one.
                missing.Add(workspace.Name);
            }
            else if (!string.Equals(existing.HostUrl, hostUrl, StringComparison.OrdinalIgnoreCase))
            {
                var moved = new WorkspaceProfile
                {
                    Name = existing.Name,
                    HostUrl = hostUrl,
                    Token = existing.Token,
                    GitAuthorName = existing.GitAuthorName,
                    GitAuthorEmail = existing.GitAuthorEmail,
                    WorkspaceId = existing.WorkspaceId,
                };
                WorkspaceProfiles.Save(orkkaRoot, moved, secretStores(orkkaRoot, existing.Name));
                profiles.RemoveAll(p => p.Name == existing.Name);
                profiles.Add(moved);
                updated.Add(existing.Name);
            }
        }
        return new Outcome(added, updated, missing);
    }

    /// <summary>A valid profile name from the workspace's name ("Acme Corp" → <c>acme-corp</c>),
    /// suffixed when another workspace's profile already has it; <c>ws-{id}</c> when the name
    /// has nothing usable.</summary>
    internal static string UniqueName(BootstrapWorkspace workspace, IReadOnlyCollection<WorkspaceProfile> profiles)
    {
        var baseName = Slug(workspace.Name);
        if (baseName.Length == 0)
            baseName = "ws-" + Slug(workspace.WorkspaceId)[..Math.Min(8, Slug(workspace.WorkspaceId).Length)];
        if (!WorkspaceProfiles.IsValidName(baseName))
            baseName = "workspace";

        var taken = profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(baseName))
            return baseName;
        for (var i = 2; ; i++)
        {
            var suffix = $"-{i}";
            var candidate = baseName[..Math.Min(baseName.Length, 32 - suffix.Length)] + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    internal static string Slug(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 32 ? slug[..32].TrimEnd('-') : slug;
    }
}
