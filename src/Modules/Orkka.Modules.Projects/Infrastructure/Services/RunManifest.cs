namespace Orkka.Modules.Projects.Infrastructure.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using Orkka.Modules.Projects.Contracts;

/// <summary>
/// The repo's own answer to "how do I run this": the <c>run</c> section of
/// <see cref="OrkkaManifest"/> (<c>orkka.json</c> at the repository root), whose targets have
/// the same shape as <see cref="RunTargetDto"/> minus <c>repoId</c> (implied — it is this
/// repo). The file's other sections (<c>stack</c>) are read by their own owners.
///
/// This is the runnable contract the agents maintain. When it exists, detection reads it
/// instead of asking a model: deterministic, free, and owned by whoever last changed how the
/// app starts — which is exactly the party that knows. It lives at the root, checked in,
/// because <c>.orkka/</c> is git-excluded in agent worktrees and a manifest that could not be
/// committed would be no contract at all.
///
/// <code>
/// {
///   "stack": "node",
///   "run": {
///     "targets": [
///       { "key": "db",  "label": "Postgres", "kind": "infra",
///         "command": "docker compose up postgres", "stopCommand": "docker compose stop postgres",
///         "port": 5432 },
///       { "key": "web", "label": "Web", "kind": "web", "command": "npm run dev",
///         "installCommand": "npm install", "port": 3000, "dependsOn": ["db"] }
///     ],
///     "notes": "…"
///   }
/// }
/// </code>
/// </summary>
public static class RunManifest
{
    public const string FileName = OrkkaManifest.FileName;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The parsed run section, with <c>repoId</c> filled in. Null when the file has
    /// no usable run section — malformed JSON, no targets, or targets without commands are
    /// all "no manifest", never an exception: a broken manifest degrades to the model pass,
    /// not to a Run button that errors.</summary>
    public static ParsedRunManifest? TryParse(string? json, string repoId)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        ManifestRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<ManifestRoot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        var run = root?.Run;
        if (run?.Targets is not { Length: > 0 } targets)
            return null;

        var dtos = targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Command))
            .Select(t => new RunTargetDto(
                t.Key ?? "",
                t.Label ?? t.Key ?? "",
                t.Kind ?? RunTargetKinds.Other,
                repoId,
                t.Command!,
                t.WorkingDirectory,
                t.InstallCommand,
                t.Port,
                t.Url,
                t.DependsOn,
                t.ManualStep,
                t.AutoSelect ?? true,
                t.StopCommand,
                t.RestartOnMerge ?? false,
                t.PreviewPath))
            .ToArray();

        return dtos.Length == 0 ? null : new ParsedRunManifest(dtos, Blank(run.Notes));
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class ManifestRoot
    {
        public RunSection? Run { get; set; }
    }

    private sealed class RunSection
    {
        public ManifestTarget[]? Targets { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class ManifestTarget
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Kind { get; set; }
        public string? Command { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? InstallCommand { get; set; }
        public string? StopCommand { get; set; }
        public int? Port { get; set; }
        public string? Url { get; set; }
        public string[]? DependsOn { get; set; }
        public string? ManualStep { get; set; }
        public bool? AutoSelect { get; set; }
        public bool? RestartOnMerge { get; set; }
        public string? PreviewPath { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}

public sealed record ParsedRunManifest(RunTargetDto[] Targets, string? Notes);
