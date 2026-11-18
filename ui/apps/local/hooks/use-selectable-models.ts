import { useMemo } from "react";
import { useRunnerStatus } from "@/hooks/use-runner-status";
import { annotateModels, runnerCoverage } from "@/lib/model-billing";
import type { AnnotatedModel, RunnerCoverage } from "@/lib/model-billing";
import type { AiModel, MyRunnerStatus } from "@/lib/types";

export interface SelectableModels {
  /**
   * The catalog annotated with whether the user's runner can run each model. Null until the
   * runner status is known — annotating without it would label every model "waiting" and
   * then flip them a moment later.
   */
  models: AnnotatedModel[] | null;
  /** Runner coverage the annotation was made from (null while unknown, or never connected). */
  coverage: RunnerCoverage | null;
  runner: MyRunnerStatus | null;
}

/**
 * The chat catalog (handed in by the server page — the app has no client-side catalog cache) joined with the user's
 * runner coverage (live, or last-known while it is offline).
 *
 * `currentModel` — the agent's or task's saved model — is always kept in the list so a form
 * never silently rewrites it, even when it could not run today.
 */
export function useSelectableModels(
  catalog: readonly AiModel[] | null | undefined,
  currentModel?: string | null
): SelectableModels {
  const runner = useRunnerStatus();

  const coverage = useMemo(
    () =>
      runner
        ? runnerCoverage({
            online: runner.online,
            providers: runner.providers,
            lastKnownProviders: runner.lastKnownProviders,
            lastKnownAt: runner.lastKnownAt,
          })
        : null,
    [runner]
  );

  const models = useMemo(() => {
    if (runner === null) return null;
    return annotateModels(catalog, coverage, { currentModel });
  }, [runner, catalog, coverage, currentModel]);

  return { models, coverage, runner };
}
