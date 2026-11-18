namespace Orkka.Modules.SourceControl.Contracts;

/// <summary>
/// How the end of a Story reports that its shared branch does not merge into a repository's
/// default branch yet. The merge sweep reads it as a conflict — whichever repository it is in —
/// and starts the resolver, which works through every conflicting repository of the story.
/// </summary>
public static class StoryLandingErrors
{
    /// <summary>Carried by every story conflict message (alongside git's own "Merge conflict
    /// between" wording for the repository of the landing member).</summary>
    public const string ConflictMarker = "The story's branch does not merge into its default branch in";

    public static bool IsStoryConflict(string? error)
        => error?.Contains(ConflictMarker, StringComparison.Ordinal) == true;

    public static string Conflict(string branch, IReadOnlyList<(string Repo, string DefaultBranch)> repos)
        => $"Merge conflict between {branch} and the default branch. {ConflictMarker}: "
           + string.Join(", ", repos.Select(r => $"{r.Repo} ({r.DefaultBranch})"))
           + ". Every pull request of the story stays a draft until each repository is clean.";
}
