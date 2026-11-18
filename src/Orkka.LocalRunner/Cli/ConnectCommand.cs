namespace Orkka.LocalRunner.Cli;

/// <summary>
/// `./orkka connect` — run the runner against the Orkka host on this machine
/// (<c>./orkka start</c> runs it for you, in its own process). Every workspace profile on
/// this machine (one per job — each <c>orkr_</c> token is bound to one workspace) becomes its
/// own session. `start` writes the profiles from the local host's bootstrap;
/// `connect --workspace acme --token orkr_…` still adds or updates one by hand, and a bare
/// `./orkka connect` runs them all.
/// </summary>
internal static class ConnectCommand
{
    /// <summary>The local Orkka host <c>./orkka start</c> runs — what `--host` defaults to.
    /// Loopback only: nothing on this machine listens on another interface.</summary>
    public const string DefaultHostUrl = "http://127.0.0.1:6750";

    /// <summary>Keeps <c>connect</c> running with no workspace profile yet, waiting for one to
    /// appear — what <c>start</c> passes, since a workspace created in the UI gets its profile
    /// only on the next bootstrap.</summary>
    public const string AllowEmptyFlag = "--allow-empty";

    public const string Usage =
        """
        Usage: ./orkka start
               ./orkka connect [--token orkr_... --workspace <name> --host http://127.0.0.1:6750]

        `./orkka start` runs Orkka on this machine and connects the runner to every
        workspace by itself — a workspace created in Orkka gets its profile within a minute.
        `connect` is the advanced, manual path: the runner alone; with no arguments it
        connects every workspace profile on this machine, and --token adds one by hand.
        """;

    /// <summary>The web app a person opens for this runner host: <c>runner.X</c> serves the
    /// runner socket, <c>www.X</c> the app. Any other host (a dev box) is shown as-is.</summary>
    public static string AppUrlFor(string hostUrl) =>
        Uri.TryCreate(hostUrl, UriKind.Absolute, out var uri)
        && uri.Host.StartsWith("runner.", StringComparison.OrdinalIgnoreCase)
            ? $"{uri.Scheme}://www.{uri.Host["runner.".Length..]}"
            : hostUrl;

    public sealed record ConnectArgs(string? Workspace, string? Token, string? Host, bool AllowEmpty = false);

    public static ConnectArgs ParseArgs(IEnumerable<string> args)
    {
        string? workspace = null, token = null, host = null;
        var allowEmpty = false;
        var list = args.ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            switch (list[i])
            {
                case "--host" when i + 1 < list.Length:
                    host = list[++i];
                    break;
                case "--token" when i + 1 < list.Length:
                    token = list[++i];
                    break;
                case "--workspace" or "-w" when i + 1 < list.Length:
                    workspace = list[++i];
                    break;
                case AllowEmptyFlag:
                    allowEmpty = true;
                    break;
            }
        }
        return new ConnectArgs(workspace?.Trim().ToLowerInvariant(), token?.Trim(), host?.Trim(), allowEmpty);
    }

    /// <summary>
    /// Saves the profile the arguments describe (when they describe one) and answers every
    /// profile to run. Migrates the single-connection layout first. Null with an error the
    /// user can act on when there is nothing to run or the arguments are wrong.
    /// </summary>
    public static IReadOnlyList<WorkspaceProfile>? Resolve(IEnumerable<string> args, string orkkaRoot, out string? error, out string? notice)
    {
        error = null;
        notice = WorkspaceProfiles.MigrateLegacy(orkkaRoot);
        var parsed = ParseArgs(args);
        var existing = WorkspaceProfiles.List(orkkaRoot);

        if (parsed.Workspace is not null && !WorkspaceProfiles.IsValidName(parsed.Workspace))
        {
            error = $"'{parsed.Workspace}' is not a workspace name — use lowercase letters, digits and hyphens (e.g. acme).";
            return null;
        }

        if (parsed.Token is not null || parsed.Host is not null)
        {
            var name = parsed.Workspace;
            if (name is null)
            {
                if (existing.Count > 1)
                {
                    error = "This machine has several workspaces ("
                        + string.Join(", ", existing.Select(p => p.Name))
                        + ") — say which one this token is for: --workspace <name>.";
                    return null;
                }
                name = existing.Count == 1 ? existing[0].Name : WorkspaceProfiles.DefaultName;
            }

            var current = existing.FirstOrDefault(p => p.Name == name);
            var token = parsed.Token ?? current?.Token;
            if (string.IsNullOrWhiteSpace(token))
            {
                error = Usage;
                return null;
            }
            if (!token.StartsWith("orkr_", StringComparison.Ordinal))
            {
                error = "That doesn't look like a runner token — they start with `orkr_`. "
                    + "`./orkka start` sets the runner up by itself; you rarely need a token by hand.";
                return null;
            }
            WorkspaceProfiles.Save(orkkaRoot, new WorkspaceProfile
            {
                Name = name,
                HostUrl = (parsed.Host ?? current?.HostUrl ?? DefaultHostUrl).TrimEnd('/'),
                Token = token,
                GitAuthorName = current?.GitAuthorName,
                GitAuthorEmail = current?.GitAuthorEmail,
                WorkspaceId = current?.WorkspaceId,
            });
            existing = WorkspaceProfiles.List(orkkaRoot);
        }
        else if (parsed.Workspace is not null && existing.All(p => p.Name != parsed.Workspace))
        {
            error = $"There is no workspace '{parsed.Workspace}' on this machine yet — add it with "
                + $"`./orkka connect --workspace {parsed.Workspace} --token orkr_...`.";
            return null;
        }

        if (existing.Count == 0)
        {
            // Under `start` the runner waits for the first workspace's profile instead
            // (RunnerSupervisor picks new profiles up while it runs).
            if (parsed.AllowEmpty)
                return existing;
            error = Usage;
            return null;
        }
        return existing;
    }
}
