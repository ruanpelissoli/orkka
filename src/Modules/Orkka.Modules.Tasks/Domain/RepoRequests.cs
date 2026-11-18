namespace Orkka.Modules.Tasks.Domain;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A repository an agent asked to add to its cross-repo task, and what happened to the ask.
/// </summary>
/// <param name="RepoName">As the agent named it. Kept verbatim rather than resolved to an id at
/// request time, because the agent can name a repository the project does not have — and "Atlas
/// asked for a repo called mobile, which does not exist" is a more useful thing to show a human
/// than a dropped request.</param>
/// <param name="RequestedByTaskId">The member whose run raised it, so the panel can say which
/// half hit the wall.</param>
public sealed record RepoRequest(
    [property: JsonPropertyName("repoName")] string RepoName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("requestedByTaskId")] string RequestedByTaskId,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// Serialization for <see cref="TaskGroup.RepoRequestsJson"/>, with the same total-parse
/// contract as <see cref="SpannedRepoIds"/>: malformed JSON yields an empty list rather than an
/// exception. A corrupt row must not stop a run — the request is a suggestion, and losing one is
/// far cheaper than failing the task that raised it.
/// </summary>
public static class RepoRequests
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Declined = "Declined";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string? Serialize(IReadOnlyList<RepoRequest> requests) =>
        requests.Count > 0 ? JsonSerializer.Serialize(requests, Options) : null;

    public static IReadOnlyList<RepoRequest> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<RepoRequest>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Adds a request unless the same repository is already pending or approved for this group.
    /// Returns null when there was nothing to add.
    ///
    /// Deduped because an agent that re-enters Developing after a review bounce will raise the
    /// same request again, and a human should not have to decline the same repository twice.
    /// A DECLINED request is deliberately not a bar: the second run may have a better reason,
    /// and refusing to let it ask again would leave it with no way to escalate at all.
    /// </summary>
    public static string? TryAdd(string? existingJson, RepoRequest request)
    {
        var requests = Parse(existingJson).ToList();

        if (requests.Any(r =>
                string.Equals(r.RepoName, request.RepoName, StringComparison.OrdinalIgnoreCase)
                && r.Status is Pending or Approved))
            return null;

        requests.Add(request);
        return Serialize(requests);
    }

    /// <summary>Marks every pending request for a repository with the given outcome.</summary>
    public static string? Resolve(string? existingJson, string repoName, string status)
    {
        var requests = Parse(existingJson)
            .Select(r =>
                string.Equals(r.RepoName, repoName, StringComparison.OrdinalIgnoreCase)
                && r.Status == Pending
                    ? r with { Status = status }
                    : r)
            .ToList();

        return Serialize(requests);
    }
}
