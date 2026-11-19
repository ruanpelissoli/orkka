namespace Orkka.Modules.Trackers.Domain;

using System.Text.RegularExpressions;
using Orkka.LocalRunner.Contracts;

/// <summary>
/// The status map and intake a new ticket source starts from, worked out from the statuses the
/// tracker reports for the scope (the <c>statuses</c> query):
/// <list type="bullet">
/// <item>Planning = the first status named like "In Progress" / "Doing" / "Active" / "Committed",
/// else the first in-progress-category status;</item>
/// <item>PR opened = the first status named like "Review" ("Code Review", "In Review"), else for
/// Azure DevOps the first Resolved-category state;</item>
/// <item>Picked up, Developing, In review and Merged = not moved (teams use their boards their
/// own way — each can be mapped by hand);</item>
/// <item>intake = "To Do" when the scope has it (Azure DevOps: its Proposed states), else the
/// to-do-category statuses.</item>
/// </list>
/// GitHub Projects follow the default project template's names instead: intake = Todo / Ready,
/// Developing = In Progress, In review = In Review, Merged = Done (each when the status field has
/// such an option); the rest unmapped.
/// </summary>
public static partial class TrackerStatusDefaults
{
    public sealed record Defaults(TrackerStatusMap StatusMap, string[] IntakeStatuses);

    public static Defaults Compute(string kind, IReadOnlyList<TrackerStatus> statuses)
    {
        if (kind == TrackerKinds.GitHub)
            return ComputeGitHub(statuses);

        var started = statuses.FirstOrDefault(s => StartedName().IsMatch(s.Name))?.Name
            ?? statuses.FirstOrDefault(s => s.Category == "inprogress"
                && !string.Equals(s.TrackerCategory, "Resolved", StringComparison.OrdinalIgnoreCase))?.Name;

        var prOpened = statuses.FirstOrDefault(s => ReviewName().IsMatch(s.Name))?.Name
            ?? (kind == TrackerKinds.AzureDevOps
                ? statuses.FirstOrDefault(s => string.Equals(s.TrackerCategory, "Resolved", StringComparison.OrdinalIgnoreCase))?.Name
                : null);

        string[] intake;
        if (kind == TrackerKinds.AzureDevOps)
        {
            intake = [.. statuses.Where(s => string.Equals(s.TrackerCategory, "Proposed", StringComparison.OrdinalIgnoreCase)).Select(s => s.Name)];
            if (intake.Length == 0)
                intake = [.. statuses.Where(s => s.Category == "todo").Select(s => s.Name)];
        }
        else
        {
            var toDo = statuses.FirstOrDefault(s => string.Equals(s.Name.Trim(), "To Do", StringComparison.OrdinalIgnoreCase));
            intake = toDo is not null ? [toDo.Name] : [.. statuses.Where(s => s.Category == "todo").Select(s => s.Name)];
        }
        if (intake.Length == 0)
            intake = ["To Do"];

        return new Defaults(new TrackerStatusMap(Planning: started, PrOpened: prOpened), intake);
    }

    private static Defaults ComputeGitHub(IReadOnlyList<TrackerStatus> statuses)
    {
        string? Named(Regex pattern) => statuses.FirstOrDefault(s => pattern.IsMatch(s.Name.Trim()))?.Name;

        string[] intake = [.. statuses.Where(s => GitHubPickupName().IsMatch(s.Name.Trim())).Select(s => s.Name)];
        if (intake.Length == 0)
            intake = [.. statuses.Where(s => s.Category == "todo").Select(s => s.Name)];
        if (intake.Length == 0)
            intake = ["Todo"];

        return new Defaults(new TrackerStatusMap(
            Developing: Named(StartedName()),
            InReview: Named(ReviewName()),
            Merged: Named(GitHubDoneName())), intake);
    }

    [GeneratedRegex(@"^(to\s*do|ready)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubPickupName();

    [GeneratedRegex(@"^(done|closed|complete|completed)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubDoneName();

    [GeneratedRegex(@"in\s*progress|doing|active|committed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartedName();

    [GeneratedRegex(@"review", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReviewName();
}
