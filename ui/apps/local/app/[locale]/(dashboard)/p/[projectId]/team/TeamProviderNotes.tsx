import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { useApiQuery } from "@/hooks/use-api-query";
import { useRunnerStatus } from "@/hooks/use-runner-status";
import { api } from "@/lib/api";
import { runnerCoverage } from "@/lib/model-billing";
import type { ProjectAgent } from "@/lib/types";

/**
 * Two quiet lines above the team, each shown only when it applies:
 *
 * - **Browser QA needs Claude.** The QA agent stays on Claude whatever the workspace's provider
 *   (Playwright MCP runs through the Claude Agent SDK; no other model can drive it). When this
 *   machine has no Claude signed in, the Testing lane cannot run — said here, with the command.
 * - **The provider can be chosen.** With the workspace on Automatic and both Copilot and Claude
 *   signed in, the team is on Claude; a developer whose company pays for Copilot may not know
 *   Settings → Workspace can switch every team to Copilot Auto.
 *
 * Both read the runner's coverage (live, or its last report while offline) and say nothing while
 * it is unknown.
 */
export function TeamProviderNotes({ agents }: { agents: ProjectAgent[] }) {
  const t = useTranslations("dashboard.teamProvider");
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
  // The hint is optional: a failed read just leaves it out.
  const { data: setting } = useApiQuery(() => api.org.aiProvider("").catch(() => null), []);

  if (!coverage) return null;

  const qa = agents.find((a) => a.role === "QA");
  const qaBlocked = qa !== undefined && !coverage.ready.has("claude");
  const chooseHint =
    setting?.provider === "automatic" &&
    coverage.ready.has("copilot") &&
    coverage.ready.has("claude");

  if (!qaBlocked && !chooseHint) return null;

  return (
    <div className="mb-4 space-y-1" data-testid="team-provider-notes">
      {qaBlocked && (
        <p className="text-xs leading-snug text-amber-300" data-testid="qa-needs-claude">
          {t("qaNeedsClaude", { name: qa.name })}{" "}
          <code className="rounded bg-black/40 px-1.5 py-0.5 font-mono text-gray-200">claude</code>
        </p>
      )}
      {chooseHint && (
        <p className="text-xs text-gray-400" data-testid="choose-provider-hint">
          {t("chooseProvider")}{" "}
          <Link
            href="/settings/workspace"
            className="font-medium text-gray-300 underline decoration-current/40 underline-offset-2 hover:text-white"
          >
            {t("chooseProviderLink")}
          </Link>
        </p>
      )}
    </div>
  );
}
