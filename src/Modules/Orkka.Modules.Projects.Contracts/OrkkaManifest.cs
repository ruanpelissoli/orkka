namespace Orkka.Modules.Projects.Contracts;

using System.Text.Json;

/// <summary>
/// <c>orkka.json</c> at a repository root: THE file Orkka reads about a repo, checked in and
/// maintained by the agents that work on it. Every Orkka project, greenfield or existing, is
/// expected to carry one — the bootstrap task writes it on an empty repo, and the dev-agent
/// contract adds it to a repo that lacks one on the next task that touches it.
///
/// Sections today:
/// <list type="bullet">
/// <item><c>stack</c> — the toolchain the code is written in, a <see cref="RepoStacks"/>
/// name. Decides which worker pool (which toolchain image) cloud tasks run on. Read before
/// any manifest sniffing: a repo that says what it is beats a guess from its file tree.</item>
/// <item><c>description</c> — one line on what the repository is; seeds the repo's routing
/// description when it is discovered in a project's base folder.</item>
/// <item><c>run</c> — the run targets the Run button starts (parsed by
/// <c>RunManifest</c> in the Projects module).</item>
/// <item><c>setup</c>, <c>check</c>, <c>build</c> — the repository's commands: prepare a task
/// worktree, the quality gate after every dev run, and the merge build. Used when the repo's
/// settings leave them empty. Read from the DEFAULT branch, which is also the only place the
/// developer's runner accepts them from without the developer's approval on the machine.</item>
/// </list>
/// New per-repo facts Orkka needs belong here as new top-level sections, never in a second
/// file: one file is what makes "does this repo have its Orkka config" a yes/no question.
/// </summary>
public static class OrkkaManifest
{
    public const string FileName = "orkka.json";

    private static readonly JsonDocumentOptions Json = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The declared <c>stack</c>, lower-cased, when it names a known
    /// <see cref="RepoStacks"/> value; null when absent, unknown, or the JSON is unusable.
    /// Null rather than "general" so a caller can fall back to classifying the tree — a
    /// typo in the file must not silently route a Go repo onto the Node-only pool.</summary>
    public static string? TryReadStack(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json, Json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("stack", out var stack)
                || stack.ValueKind != JsonValueKind.String)
                return null;

            var value = stack.GetString()?.Trim().ToLowerInvariant();
            return !string.IsNullOrEmpty(value) && RepoStacks.All.Contains(value) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The repository commands <c>orkka.json</c> declares. Each is null when absent or
    /// not a non-blank string; all are null when the JSON is unusable. Never throws.</summary>
    public static RepoCommands TryReadCommands(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return RepoCommands.None;

        try
        {
            using var doc = JsonDocument.Parse(json, Json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return RepoCommands.None;

            string? Read(string name) =>
                doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
                    ? text.Trim()
                    : null;

            return new RepoCommands(Read("setup"), Read("check"), Read("build"));
        }
        catch (JsonException)
        {
            return RepoCommands.None;
        }
    }

    /// <summary>The top-level <c>description</c> — one line on what this repository is, which
    /// agents read next to a project's other repositories to decide where work belongs. Null
    /// when absent, blank, or the JSON is unusable. Never throws.</summary>
    public static string? TryReadDescription(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json, Json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("description", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
                    ? text.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The declared stack of the repository at <paramref name="rootPath"/>, or null
    /// when it has no usable <c>orkka.json</c>. Never throws.</summary>
    public static string? TryReadStackFromRepo(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, FileName);
            return File.Exists(path) ? TryReadStack(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>The commands a repository's <c>orkka.json</c> declares; null where it declares none.</summary>
public sealed record RepoCommands(string? Setup, string? Check, string? Build)
{
    public static readonly RepoCommands None = new(null, null, null);
}
