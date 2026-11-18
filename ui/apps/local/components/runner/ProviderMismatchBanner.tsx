import { useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Button, useToast } from "@/components/ui";
import { api, invalidateApiCache } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { useRunnerStatus } from "@/hooks/use-runner-status";
import { providerLabel } from "@/lib/constants";
import { cn, providerFromModelId } from "@/lib/utils";
import {
  RUNNER_PROVIDER_PREFERENCE,
  cliForProvider,
  providerForCli,
  runnerCoverage,
} from "@/lib/model-billing";
import type { AiModel, ProjectAgent } from "@/lib/types";
import { refreshData } from "@/lib/data-refresh";

/** The model's provider, from the catalog when it lists the id. Falls back to the id-prefix
 * rule for ids the catalog legitimately can't enumerate — locally-pulled Ollama tags and the
 * models an OpenCode install's own provider logins expose. */
function providerOf(modelId: string, catalog: readonly AiModel[] | undefined): string {
  const known = catalog?.find((m) => m.id.toLowerCase() === modelId.trim().toLowerCase());
  return known?.provider ?? providerFromModelId(modelId);
}

/**
 * Shown when at least one provider is ready on the user's runner and enabled agents still use
 * models from a provider that isn't — a claude-seeded pod on a codex-only machine. Those tasks
 * would sit waiting for a sign-in that may never come, which is what the banner warns about.
 * One click remaps the team to the first ready provider's tier defaults; QA stays on Claude.
 *
 * `compact` is the board header's one-liner: the sentence plus a link to the Team page,
 * where the full banner and its button live.
 */
export function ProviderMismatchBanner({
  agents,
  projectId,
  catalog,
  compact = false,
  className,
}: {
  agents: ProjectAgent[];
  projectId: string;
  /** The model catalog, when the page has it — improves provider lookup for saved models. */
  catalog?: readonly AiModel[];
  compact?: boolean;
  className?: string;
}) {
  const t = useTranslations("dashboard.providerMismatch");
  const runner = useRunnerStatus();
  const withToken = useClientApi();
  const { toast } = useToast();
  const [switching, setSwitching] = useState(false);

  // Live coverage when online, the last Hello when not: an offline runner still tells us
  // which providers the team should be on.
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

  // Only act on loaded state — never flash while polling.
  if (runner === null || !coverage || coverage.ready.size === 0) {
    return null;
  }

  const mismatched = agents.filter(
    (a) =>
      a.role !== "QA" && // QA is claude-by-design; the pipeline skips it gracefully
      !coverage.ready.has(cliForProvider(providerOf(a.model, catalog)))
  );
  if (mismatched.length === 0) return null;

  // Steer toward the best ready provider in preference order — never one whose CLI is
  // missing, or every agent would move onto something the runner cannot spawn.
  const targetCli =
    RUNNER_PROVIDER_PREFERENCE.find((cli) => coverage.ready.has(cli)) ??
    [...coverage.ready][0];
  const targetProvider = providerForCli(targetCli);
  const targetLabel = providerLabel(targetProvider);

  async function handleSwitch() {
    setSwitching(true);
    try {
      const result = await withToken((token) =>
        api.agents.switchProvider(projectId, targetProvider, token)
      );
      invalidateApiCache();
      toast(
        result.skippedQaCount > 0
          ? t("switchedQa", { count: result.switchedCount, provider: targetLabel })
          : t("switched", { count: result.switchedCount, provider: targetLabel })
      );
      refreshData();
    } catch {
      toast(t("switchFailed"), "error");
    } finally {
      setSwitching(false);
    }
  }

  if (compact) {
    return (
      <p
        className={cn(
          "flex flex-wrap items-center gap-x-2 rounded-xl border border-amber-500/20 bg-amber-500/10 px-3 py-2 text-xs text-amber-300",
          className
        )}
      >
        <span className="min-w-0">{t("hostedWarning", { count: mismatched.length })}</span>
        <Link
          href={`/p/${projectId}/team`}
          className="shrink-0 font-medium underline decoration-current/40 underline-offset-2 hover:text-white"
        >
          {t("reviewTeam")}
        </Link>
      </p>
    );
  }

  return (
    <div
      className={cn(
        "mb-4 flex flex-wrap items-center gap-3 rounded-xl border border-amber-500/20 bg-amber-500/10 px-4 py-3",
        className
      )}
    >
      <p className="min-w-0 flex-1 text-sm text-amber-300">
        {t("hostedWarning", { count: mismatched.length })}
      </p>
      <Button size="sm" onClick={handleSwitch} disabled={switching} loading={switching}>
        {t("switchTeam", { provider: targetLabel })}
      </Button>
    </div>
  );
}
