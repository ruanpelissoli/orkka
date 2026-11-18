namespace Orkka.SharedKernel.Ai;

/// <summary>
/// Serves a one-shot utility completion (dependency analysis, decomposition, titles, run-config
/// detection, documentation sync…) on the requesting user's own connected runner — their own
/// AI CLI subscription.
///
/// The runner is the ONLY place customer utility work runs: there is no fallback to Orkka's
/// platform key (user decision, 2026-10-26). A miss means the feature degrades — a skipped
/// title, a merge page without a summary, a plan that fails with "your runner is offline" —
/// never a call billed to Orkka. Callers that cannot do without the answer use
/// <see cref="UserRunnerUtilityExtensions.CompleteOnRunnerAsync"/>, which turns a miss into a
/// <see cref="RunnerUtilityUnavailableException"/> carrying an actionable message.
/// Runner-served calls are recorded with <c>FlatCostBase: 0</c>, the convention runner agent
/// runs use: attribution stays visible, spend is zero.
/// </summary>
public interface IUserRunnerUtility
{
    /// <summary>
    /// Attempts to serve the completion on the user's connected runner. Null — never an
    /// exception — when this host holds no runner links, the user has no connected runner,
    /// the runner has no AI CLI signed in, or the call failed. A utility feature must not
    /// crash because a laptop lid closed; it takes its own failure path instead.
    /// </summary>
    Task<AnthropicResponse?> TryCompleteAsync(
        Ulid orgId, string? userId, AnthropicRequest request, CancellationToken ct = default);
}

/// <summary>The default for hosts that hold no runner links (tests, hosts without the Runners
/// module): every call is a miss, so every utility feature takes its runner-offline path.</summary>
public sealed class NoUserRunnerUtility : IUserRunnerUtility
{
    public Task<AnthropicResponse?> TryCompleteAsync(
        Ulid orgId, string? userId, AnthropicRequest request, CancellationToken ct = default)
        => Task.FromResult<AnthropicResponse?>(null);
}

/// <summary>
/// The runner could not serve a utility call that the feature needs. The message is meant for
/// the user as-is: it says what happened and what to do about it.
/// </summary>
public sealed class RunnerUtilityUnavailableException(string message) : Exception(message)
{
    /// <summary>"Your runner is offline or unavailable — run `./orkka start` to
    /// {purpose}." — one wording for every utility feature that has to say it.</summary>
    public static string MessageFor(string purpose)
        => $"Your runner is offline or unavailable — run `./orkka start` to {purpose}.";

    public static RunnerUtilityUnavailableException For(string purpose) => new(MessageFor(purpose));
}

public static class UserRunnerUtilityExtensions
{
    /// <summary>
    /// <see cref="IUserRunnerUtility.TryCompleteAsync"/> for callers that need the answer:
    /// a miss throws <see cref="RunnerUtilityUnavailableException"/> naming
    /// <paramref name="purpose"/> (e.g. "plan this milestone").
    /// </summary>
    public static async Task<AnthropicResponse> CompleteOnRunnerAsync(
        this IUserRunnerUtility utility, Ulid orgId, string? userId, AnthropicRequest request,
        string purpose, CancellationToken ct = default)
        => await utility.TryCompleteAsync(orgId, userId, request, ct)
            ?? throw RunnerUtilityUnavailableException.For(purpose);
}
