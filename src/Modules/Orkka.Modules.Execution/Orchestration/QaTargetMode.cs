namespace Orkka.Modules.Execution.Orchestration;

using Orkka.Modules.Projects.Contracts;

/// <summary>
/// Which shape the QA lane's Testing run should take for one repo.
///
/// The lane was written around a browser: start the app, navigate, click, assert. A repo that
/// serves a JSON API has nothing to drive that way, so pointing Playwright at it produces an
/// expensive run that verifies almost nothing. The project's stored run config already knows
/// the difference — <see cref="RunTargetKinds"/> is detected once per project — so the answer
/// is a read, not another model call.
/// </summary>
internal static class QaTargetMode
{
    /// <summary>
    /// True when every runnable target for this repo is a backend one, so QA should exercise
    /// HTTP instead of a browser.
    ///
    /// A repo that ALSO serves a UI (a monorepo holding both) stays on the browser lane: the
    /// UI is the higher-level thing to verify and driving it exercises the API underneath
    /// anyway. So does a repo with no detected targets — today's behaviour is the safe
    /// default, and guessing wrong costs a whole QA cycle.
    /// </summary>
    public static bool IsApiOnly(IReadOnlyList<RunTargetDto>? targets, Ulid repoId)
    {
        if (targets is null || targets.Count == 0)
            return false;

        var repoKey = repoId.ToString();
        var mine = targets.Where(t =>
            string.Equals(t.RepoId, repoKey, StringComparison.OrdinalIgnoreCase)).ToList();

        if (mine.Count == 0)
            return false;

        // Normalize: Kind arrives from a JSON column originally filled by model output, so an
        // unrecognised value must read as "other", never match a branch by accident.
        var kinds = mine.Select(t => RunTargetKinds.Normalize(t.Kind)).ToList();

        if (kinds.Any(k => k is RunTargetKinds.Web or RunTargetKinds.Mobile or RunTargetKinds.Desktop))
            return false;

        return kinds.Any(k => k == RunTargetKinds.Api);
    }
}
