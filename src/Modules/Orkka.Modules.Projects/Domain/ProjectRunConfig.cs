namespace Orkka.Modules.Projects.Domain;

using Orkka.Modules.Projects.Contracts;
using Orkka.SharedKernel.Domain;

/// <summary>
/// How to run this project on a developer's own machine — worked out once by reading the
/// repos, then stored so pressing Run costs nothing.
///
/// Per project rather than per repo, unlike <see cref="ProjectRepo.SetupCommand"/>: running
/// an app is a cross-repo act. A Next.js frontend that talks to a Go API is one thing the
/// user wants to start, and a per-repo field cannot express either the "start these two
/// together" or the ordering between them.
/// </summary>
public sealed class ProjectRunConfig : TenantEntity
{
    public Ulid ProjectId { get; set; }

    /// <summary>The <see cref="RunTargetDto"/> array as JSON. A blob rather than a table:
    /// targets are only ever read and written whole, and their shape belongs to the detector
    /// prompt — which will keep gaining fields — not to a migration.</summary>
    public required string TargetsJson { get; set; }

    /// <summary>Anything the model wants the user to know that isn't a command — "the API
    /// needs Docker running", "the mobile app expects the API on the LAN address, not
    /// localhost".</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Hash of the repo manifests the detection was based on. Its only job is to answer "is
    /// what we stored still plausible?" without spending a model call: same fingerprint,
    /// same answer.
    /// </summary>
    public required string Fingerprint { get; set; }

    public RunConfigSource Source { get; set; } = RunConfigSource.Ai;

    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public Project Project { get; set; } = null!;
}
