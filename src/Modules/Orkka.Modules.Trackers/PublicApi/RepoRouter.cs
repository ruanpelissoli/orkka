namespace Orkka.Modules.Trackers.PublicApi;

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Ai;

/// <summary>What the router is told about a ticket. Untrusted text — fenced in the prompt.</summary>
public sealed record RepoRouteTicket(
    string Key, string Title, string Description, IReadOnlyList<string> Components, IReadOnlyList<string> Labels,
    string? AreaPath);

/// <summary>The router's answer, already filtered to the project's repositories.</summary>
/// <param name="HighConfidence">The model was sure. A single repository is only taken when it was.</param>
public sealed record RepoRouteDecision(IReadOnlyList<Ulid> RepoIds, bool HighConfidence, string? Reason);

/// <summary>
/// Picks the repository (or repositories) a tracker ticket belongs to when no routing rule
/// matched and the project has several — a utility call on the owner's own runner and AI
/// subscription. Null when the runner could not answer (offline, no CLI signed in) or the answer
/// could not be read: the caller then asks the developer.
/// </summary>
public interface IRepoRouter
{
    Task<RepoRouteDecision?> RouteAsync(
        Ulid orgId, string userId, RepoRouteTicket ticket, IReadOnlyList<RepoSummary> repos, CancellationToken ct = default);
}

internal sealed class RunnerRepoRouter(
    IUserRunnerUtility runnerUtility,
    IOptions<AnthropicOptions> anthropicOptions,
    ILogger<RunnerRepoRouter> logger) : IRepoRouter
{
    private const int MaxDescriptionChars = 6_000;

    internal const string SystemPrompt =
        """
        You route a work ticket from an issue tracker to the code repository (or repositories) where it
        has to be implemented. You are given the ticket and the project's repositories with their stack
        and, when known, a description.

        Answer with ONE JSON object and nothing else:
        {"repoIds": ["<id>", ...], "confidence": "high" | "low", "reason": "<one short sentence>"}

        - repoIds: the ids of the repositories the change needs, most important first. Usually one. List
          several only when the ticket clearly needs changes in each (e.g. an API endpoint AND the screen
          that calls it).
        - confidence: "high" only when the ticket clearly points at those repositories (it names the
          service, the screen, the stack, a component that obviously lives there). Otherwise "low".
        - reason: why, in words the developer can check.
        """;

    public async Task<RepoRouteDecision?> RouteAsync(
        Ulid orgId, string userId, RepoRouteTicket ticket, IReadOnlyList<RepoSummary> repos, CancellationToken ct = default)
    {
        if (repos.Count == 0)
            return null;

        var request = new AnthropicRequest
        {
            Model = anthropicOptions.Value.FastUtilityModel,
            MaxTokens = 400,
            System = SystemPrompt + "\n\n" + PromptSafety.Notice,
            Messages = [new AnthropicMessage("user", BuildPrompt(ticket, repos))],
        };

        AnthropicResponse? response;
        try
        {
            response = await runnerUtility.TryCompleteAsync(orgId, userId, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("Repo routing for {Key} could not run: {Error}", ticket.Key, ex.Message);
            return null;
        }
        if (response is null)
            return null;

        return Parse(response.Text, repos, logger, ticket.Key);
    }

    internal static string BuildPrompt(RepoRouteTicket ticket, IReadOnlyList<RepoSummary> repos)
    {
        var sb = new StringBuilder("Repositories:\n");
        foreach (var repo in repos)
        {
            sb.Append("- id: ").Append(repo.Id).Append(" | name: ").Append(repo.Name);
            if (!string.IsNullOrWhiteSpace(repo.Stack))
                sb.Append(" | stack: ").Append(repo.Stack);
            if (!string.IsNullOrWhiteSpace(repo.Description))
                sb.Append(" | description: ").Append(repo.Description.ReplaceLineEndings(" ").Trim());
            sb.Append('\n');
        }

        var ticketText = new StringBuilder()
            .Append(ticket.Key).Append(": ").Append(ticket.Title).Append('\n');
        if (ticket.Components.Count > 0)
            ticketText.Append("Components: ").Append(string.Join(", ", ticket.Components)).Append('\n');
        if (ticket.Labels.Count > 0)
            ticketText.Append("Labels: ").Append(string.Join(", ", ticket.Labels)).Append('\n');
        if (!string.IsNullOrWhiteSpace(ticket.AreaPath))
            ticketText.Append("Area path: ").Append(ticket.AreaPath).Append('\n');
        var description = ticket.Description.Length > MaxDescriptionChars
            ? ticket.Description[..MaxDescriptionChars] + "…"
            : ticket.Description;
        ticketText.Append('\n').Append(description);

        sb.Append('\n').Append(PromptSafety.Wrap("ticket", ticketText.ToString()));
        return sb.ToString();
    }

    private sealed record RawDecision(List<string>? RepoIds, string? Confidence, string? Reason);

    internal static RepoRouteDecision? Parse(string text, IReadOnlyList<RepoSummary> repos, ILogger? logger = null, string? key = null)
    {
        RawDecision raw;
        try
        {
            raw = ModelJson.Deserialize<RawDecision>(text, "repo routing");
        }
        catch (ModelOutputException ex)
        {
            logger?.LogInformation("Repo routing answer for {Key} could not be read: {Error}", key, ex.Message);
            return null;
        }

        // Ids the model was given; a name is accepted too — models echo what they read.
        var picked = new List<Ulid>();
        foreach (var value in raw.RepoIds ?? [])
        {
            var match = repos.FirstOrDefault(r => string.Equals(r.Id.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? repos.FirstOrDefault(r => string.Equals(r.Name, value?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null && !picked.Contains(match.Id))
                picked.Add(match.Id);
        }
        var high = string.Equals(raw.Confidence?.Trim(), "high", StringComparison.OrdinalIgnoreCase);
        var reason = string.IsNullOrWhiteSpace(raw.Reason) ? null : raw.Reason.Trim();
        if (reason is { Length: > 300 })
            reason = reason[..300] + "…";
        return new RepoRouteDecision(picked, high, reason);
    }
}
