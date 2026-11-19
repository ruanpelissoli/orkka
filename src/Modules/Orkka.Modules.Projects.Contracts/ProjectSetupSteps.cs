namespace Orkka.Modules.Projects.Contracts;

/// <summary>
/// The guided setup a new project goes through before its runner may pull tickets
/// (<c>/p/{slug}/setup?step=…</c>). The developer confirms each step; the setup page resumes at
/// the first one not confirmed, and Start (<see cref="Review"/>) records
/// <c>Project.SetupCompletedAt</c> — the gate the ticket-source poll waits on.
/// </summary>
public static class ProjectSetupSteps
{
    /// <summary>The base folder, its repositories, their branches, forges and commands.</summary>
    public const string Folder = "folder";

    /// <summary>The machine check: runner, AI CLIs, git identity, repository access, forge CLIs.</summary>
    public const string Machine = "machine";

    /// <summary>The ticket source (Jira / Azure DevOps / GitHub Projects / none) and its preview.</summary>
    public const string Source = "source";

    /// <summary>The workspace AI provider and the team's models.</summary>
    public const string Team = "team";

    /// <summary>The readiness checklist and Start.</summary>
    public const string Review = "review";

    /// <summary>The steps a developer confirms, in order (Review is finished by Start).</summary>
    public static readonly string[] Confirmable = [Folder, Machine, Source, Team];

    /// <summary>Every step, in order.</summary>
    public static readonly string[] All = [Folder, Machine, Source, Team, Review];

    public static bool IsConfirmable(string? step)
        => step is not null && Confirmable.Contains(step, StringComparer.Ordinal);
}
