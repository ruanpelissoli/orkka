namespace Orkka.Modules.Tasks.Contracts;

/// <summary>Shared limits for permanent code-change snapshots.</summary>
public static class CodeChangeSnapshotRules
{
    /// <summary>Cap on the stored unified diff, matching the DocumentRules precedent. Lives in
    /// Contracts because the orchestrator (Execution module) passes it to GetDiffAsync and the
    /// Tasks module stores against it.</summary>
    public const int MaxDiffChars = 200_000;
}
