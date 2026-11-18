namespace Orkka.Modules.Tasks.Features;

using Orkka.Modules.Tasks.Contracts;

/// <summary>
/// The lookback vocabulary shared by every "how has this been going lately" surface —
/// agent stats, board metrics. One table so a window means the same span everywhere;
/// an unrecognised value falls back to 7d rather than erroring, because a bad query
/// string should not take a dashboard down.
/// </summary>
internal static class StatsWindow
{
    internal static (string Window, TimeSpan Span) Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "24h" => ("24h", TimeSpan.FromHours(24)),
        "30d" => ("30d", TimeSpan.FromDays(30)),
        _ => ("7d", TimeSpan.FromDays(7)),
    };
}

/// <summary>
/// What counts as spend that bought nothing. Failed and cancelled runs both consumed tokens
/// and produced no merged code; keeping the definition in one place stops the activity page
/// and the board strip from quoting different numbers for the same window.
/// </summary>
internal static class ExecutionOutcome
{
    internal static bool IsWasted(ExecutionStatus status) =>
        status is ExecutionStatus.Failed or ExecutionStatus.Cancelled;

    /// <summary>Runs that reached a terminal state. The denominator for any rate — including
    /// in-flight runs makes a failure rate dip every time someone opens the board mid-run.</summary>
    internal static bool IsFinished(ExecutionStatus status) =>
        status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.Cancelled;
}
