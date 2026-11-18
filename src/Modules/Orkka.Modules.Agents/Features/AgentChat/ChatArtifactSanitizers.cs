namespace Orkka.Modules.Agents.Features.AgentChat;

using Orkka.Git;
using Orkka.Modules.Documents.Contracts;
using Orkka.Modules.Tasks.Contracts;

/// <summary>A task spec as the PM agent emits it (fenced ```tasks block or create_tasks tool
/// call) — unvalidated model output, sanitized by <see cref="ChatArtifactSanitizers"/>.</summary>
internal sealed record PmTaskSpec(
    string? Title,
    string? Description,
    string? Priority,
    string? Type,
    string? Repo,
    List<string>? Tags,
    List<string>? ContextFiles,
    // Matched by title against the project's milestones, the same way Repo is — the model
    // never handles an id and so can never invent one.
    string? Milestone = null);

internal sealed record PmTasksBlock(List<PmTaskSpec>? Tasks);

internal sealed record ChatRepoRef(Ulid Id, string Name);

/// <summary>
/// Sanitization shared by the fenced-block extractors in <see cref="ChatTurnRunner"/> and the
/// tool handlers in <see cref="Tools.OrkkaChatToolServer"/> — both consume raw agent output,
/// so both must apply the exact same normalization and safety rules.
/// </summary>
internal static class ChatArtifactSanitizers
{
    public const int MaxDocumentsPerMessage = 5;
    public const int MaxTasksPerMessage = 8;

    public const int MaxTaskTitleLength = 500;
    public const int MaxTaskDescriptionLength = 10000;
    public const int MaxContextFiles = 20;

    /// <summary>Normalizes document specs with the same rules the Documents endpoints enforce
    /// (no slashes in titles, no ".." path segments, content cap). Unusable entries are
    /// dropped; at most <see cref="MaxDocumentsPerMessage"/> survive.</summary>
    public static List<ChatDocumentSpec> SanitizeDocuments(IEnumerable<AgentDocumentSpec> specs)
    {
        var documentSpecs = new List<ChatDocumentSpec>();
        foreach (var docSpec in specs.Take(MaxDocumentsPerMessage))
        {
            // SECURITY: titles and folder paths come from agent output — normalize with the
            // same rules the Documents endpoints enforce (no slashes in titles, no ".." segments).
            var title = DocumentRules.NormalizeTitle(docSpec.Title);
            if (title is null || string.IsNullOrWhiteSpace(docSpec.Content))
                continue;

            var content = docSpec.Content.Length <= DocumentRules.MaxContentLength
                ? docSpec.Content
                : docSpec.Content[..DocumentRules.MaxContentLength];

            documentSpecs.Add(new ChatDocumentSpec(title, DocumentRules.NormalizePath(docSpec.Path), content));
        }
        return documentSpecs;
    }

    /// <summary>Resolves repos and milestones by name and normalizes PM task specs: unknown
    /// repo names fall back to the project's first repo, an unknown milestone name falls back
    /// to the Backlog, unsafe context-file paths are dropped, titles/descriptions are capped.
    /// Entries without a title are dropped.</summary>
    public static List<ChatTaskSpec> SanitizeTasks(
        IEnumerable<PmTaskSpec> specs,
        IReadOnlyList<ChatRepoRef> projectRepos,
        IReadOnlyList<ChatMilestoneRef>? projectMilestones = null)
    {
        var taskSpecs = new List<ChatTaskSpec>();
        foreach (var taskSpec in specs)
        {
            if (string.IsNullOrWhiteSpace(taskSpec.Title))
                continue;

            // Unmatched or absent milestone name = the Backlog. A typo must not silently file
            // work under the wrong outcome.
            var milestoneId = projectMilestones?
                .FirstOrDefault(m => string.Equals(m.Title, taskSpec.Milestone, StringComparison.OrdinalIgnoreCase))
                ?.Id;

            var repoId = projectRepos
                .FirstOrDefault(r => string.Equals(r.Name, taskSpec.Repo, StringComparison.OrdinalIgnoreCase))
                ?.Id ?? projectRepos[0].Id;

            var tags = new List<string> { "pm-generated" };
            if (taskSpec.Tags is not null)
                tags.AddRange(taskSpec.Tags.Where(t => !string.IsNullOrWhiteSpace(t) && t != "pm-generated"));

            // SECURITY: context file paths come from the PM agent's output — keep only safe
            // repo-relative paths so a task can never point a downstream dev agent at ../../.env etc.
            var contextFiles = taskSpec.ContextFiles?
                .Where(f => RepoRelativePath.IsSafeRelative(f))
                .Take(MaxContextFiles)
                .ToList();

            var title = taskSpec.Title.Trim();
            if (title.Length > MaxTaskTitleLength) title = title[..MaxTaskTitleLength];

            var description = taskSpec.Description;
            if (description?.Length > MaxTaskDescriptionLength) description = description[..MaxTaskDescriptionLength];

            taskSpecs.Add(new ChatTaskSpec(
                title, description,
                taskSpec.Priority ?? "Medium",
                taskSpec.Type ?? "Feature",
                repoId, tags, contextFiles, milestoneId));
        }
        return taskSpecs;
    }
}
