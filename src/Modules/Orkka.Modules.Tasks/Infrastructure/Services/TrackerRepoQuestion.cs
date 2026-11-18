namespace Orkka.Modules.Tasks.Infrastructure.Services;

using System.Text.Json;
using System.Text.RegularExpressions;
using Orkka.Modules.Projects.Contracts;

/// <summary>Marks the question a tracker task asks when routing could not pick its repository
/// (<c>TasksApi.AskTrackerRepoAsync</c>), and whether answering it submits the task.</summary>
public sealed record TrackerRepoQuestionMetadata(bool TrackerRepoQuestion, bool SubmitOnAnswer);

/// <summary>Reading the answer to the routing question: which of the project's repositories it names.</summary>
public static class TrackerRepoQuestion
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static TrackerRepoQuestionMetadata? ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<TrackerRepoQuestionMetadata>(json, Json);
            return parsed is { TrackerRepoQuestion: true } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The repositories <paramref name="answer"/> names, in the order the project lists them:
    /// a repository id, or a repository name standing on its own (so "api" does not match inside
    /// "shop-api"). The questions UI posts the picked options as text, one or several.
    /// </summary>
    public static IReadOnlyList<RepoSummary> Match(string answer, IReadOnlyList<RepoSummary> repos)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return [];

        var matched = new List<RepoSummary>();
        // Longest names first, each match blanked out, so a short name inside a longer one that
        // was already picked does not count twice.
        var text = answer;
        foreach (var repo in repos.OrderByDescending(r => r.Name.Length))
        {
            var byId = text.Contains(repo.Id.ToString(), StringComparison.OrdinalIgnoreCase);
            var pattern = $@"(?<![\w.-]){Regex.Escape(repo.Name)}(?![\w.-])";
            var byName = Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!byId && !byName)
                continue;
            matched.Add(repo);
            text = Regex.Replace(text, pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Replace(repo.Id.ToString(), " ", StringComparison.OrdinalIgnoreCase);
        }
        return [.. repos.Where(matched.Contains)];
    }
}
