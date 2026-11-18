namespace Orkka.Modules.Execution.Agents;

/// <summary>
/// Hard per-run caps for agent execution. These bound the blast radius of a runaway or
/// prompt-injected agent (unbounded tool loops, hung turns) and the cost it can incur.
/// <see cref="AgentRequest.MaxTurns"/> already caps model turns; these add tool-call and
/// wall-clock ceilings. Values ≤ 0 disable the corresponding cap.
/// </summary>
public sealed class AgentLimitsOptions
{
    public const string SectionName = "AgentLimits";

    /// <summary>Maximum number of tool invocations before the run is stopped. Default 100.</summary>
    public int MaxToolCalls { get; set; } = 100;

    /// <summary>Maximum wall-clock seconds for a single run before it is stopped. Default 1800 (30 min).</summary>
    public int MaxWallClockSeconds { get; set; } = 1800;
}
