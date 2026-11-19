import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { WorkspaceAiProviderCard } from "@/components/settings/WorkspaceAiProviderCard";
import { TeamModelControl } from "@/app/[locale]/(dashboard)/p/[projectId]/team/TeamModelControl";
import { TeamProviderNotes } from "@/app/[locale]/(dashboard)/p/[projectId]/team/TeamProviderNotes";
import { Link } from "@/i18n/navigation";
import { useApiQuery } from "@/hooks/use-api-query";
import { api } from "@/lib/api";
import { teamChecks } from "@/lib/project-setup";
import type { MyRunnerStatus, Project, WorkspaceAiProvider } from "@/lib/types";
import { SetupCheckList } from "./SetupCheckList";

/**
 * Step 4 — who does the work, on which AI CLI: the workspace's AI provider (the Settings →
 * Workspace card itself), the note that browser QA needs Claude, and the team's models with the
 * Team page's "set every agent's model" control. Each agent stays editable on the Team page.
 */
export function TeamStep({
  project,
  runner,
  aiProvider,
}: {
  project: Project;
  runner: MyRunnerStatus | null;
  aiProvider: WorkspaceAiProvider | null;
}) {
  const t = useTranslations("dashboard.projectSetup.team");
  const { data } = useApiQuery(async () => {
    const [agents, models] = await Promise.all([
      api.agents.list(project.id, "").catch(() => []),
      api.models.list("").catch(() => []),
    ]);
    return { agents, models: models.filter((m) => m.kind === "Chat" && m.isEnabled) };
  }, [project.id]);
  const checks = teamChecks({ runner, aiProvider });

  return (
    <div className="space-y-4" data-testid="setup-team-step">
      <WorkspaceAiProviderCard />

      <GlassCard padding="md">
        <h3 className="text-sm font-medium text-gray-100">{t("title")}</h3>
        <p className="mt-1 text-xs text-gray-400">{t("intro")}</p>
        {data && (
          <div className="mt-4 space-y-4">
            <TeamProviderNotes agents={data.agents} />
            <TeamModelControl agents={data.agents} models={data.models} projectId={project.id} />
            <ul className="grid gap-2 sm:grid-cols-2" data-testid="setup-team-agents">
              {data.agents.map((agent) => (
                <li
                  key={agent.id}
                  className="flex items-center justify-between gap-2 rounded-lg border border-white/[0.06] bg-white/[0.02] px-3 py-2"
                >
                  <span className="min-w-0 truncate text-sm text-gray-200">
                    {agent.name} <span className="text-xs text-gray-400">· {agent.role}</span>
                  </span>
                  <span className="shrink-0 font-mono text-xs text-gray-300">{agent.model}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        <Link href={`/p/${project.slug}/team`} className="mt-3 inline-block text-xs text-cyan-400 hover:text-cyan-300">
          {t("teamPage")}
        </Link>
      </GlassCard>

      <SetupCheckList checks={checks.filter((c) => c.level !== "ok")} testId="setup-team-checks" />
    </div>
  );
}
