namespace Orkka.Modules.SourceControl.Domain;

using Orkka.Git;

/// <summary>
/// One finished git operation, as the engine's ledger recorded it — the row behind "is git
/// reliable": success rate per operation kind per topology, and for every failure the code,
/// the message and the redacted command that failed.
///
/// Platform-wide, not tenant-scoped: the question it answers is asked by Orkka's operators
/// across every organisation, and an operation may have no organisation at all (a chat
/// workspace probe, a cleanup sweep). Written fire-and-forget by <c>DbGitOperationLedger</c>.
/// </summary>
public sealed class GitOperation
{
    public Ulid Id { get; set; } = Ulid.NewUlid();
    public GitOperationKind Kind { get; set; }
    public required string Topology { get; set; }
    public Ulid? OrgId { get; set; }
    public Ulid? RepoId { get; set; }
    public Ulid? TaskId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int DurationMs { get; set; }
    public bool Succeeded { get; set; }
    public GitErrorCode Code { get; set; }
    public string? Error { get; set; }
    public string? LastCommand { get; set; }
    public int CommandCount { get; set; }

    public static GitOperation From(GitOperationRecord record) => new()
    {
        Kind = record.Kind,
        Topology = record.Topology,
        OrgId = Ulid.TryParse(record.OrgId, out var org) ? org : null,
        RepoId = Ulid.TryParse(record.RepoId, out var repo) ? repo : null,
        TaskId = Ulid.TryParse(record.TaskId, out var task) ? task : null,
        StartedAt = record.StartedAt,
        DurationMs = (int)Math.Min(record.Duration.TotalMilliseconds, int.MaxValue),
        Succeeded = record.Succeeded,
        Code = record.Code,
        Error = Truncate(record.Error, 2000),
        LastCommand = Truncate(record.LastCommand, 1000),
        CommandCount = record.CommandCount,
    };

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}
