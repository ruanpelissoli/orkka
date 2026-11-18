namespace Orkka.Modules.SourceControl.Contracts;

/// <summary>One commit as shown in repo history views. No author e-mail: contributors'
/// addresses stay on the developer's machine.</summary>
public sealed record GitCommitInfo(
    string Sha,
    string Author,
    DateTimeOffset Date,
    string Subject);

/// <summary>One branch of a repo with its tip commit. IsRemote marks branches that exist
/// only on the repo's remote (never checked out locally) — Orkka Local surfaces these so
/// users can adopt them without leaving the app; forge-hosted repos never set it.</summary>
public sealed record GitBranchInfo(
    string Name,
    string Sha,
    string? LastCommitSubject,
    DateTimeOffset? LastCommitDate,
    bool IsRemote = false);

/// <summary>One entry of a single tree level — powers lazy file-tree browsing.</summary>
public sealed record RepoTreeEntry(
    string Name,
    string Path,
    bool IsDirectory);
