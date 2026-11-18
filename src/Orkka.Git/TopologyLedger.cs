namespace Orkka.Git;

/// <summary>
/// The host's ledger, stamping a different topology: what a host-side proxy uses to record
/// operations it dispatched to another machine (the user's runner) so the dashboard counts
/// them where the git actually ran, not where the request was made.
/// </summary>
public sealed class TopologyLedger(IGitOperationLedger inner, string topology) : IGitOperationLedger
{
    public string Topology { get; } = topology;

    public void Record(GitOperationRecord record) => inner.Record(record with { Topology = Topology });
}
