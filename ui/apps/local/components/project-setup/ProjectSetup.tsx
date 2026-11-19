import { useState } from "react";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useRouter } from "@/i18n/navigation";
import { useApiQuery } from "@/hooks/use-api-query";
import { useClientApi } from "@/hooks/use-client-api";
import { api, extractErrorMessage, invalidateApiCache } from "@/lib/api";
import { refreshData } from "@/lib/data-refresh";
import { useSearchParams, useUpdateSearchParams } from "@/lib/navigation";
import {
  CONFIRMABLE_STEPS,
  SETUP_STEPS,
  parseStep,
  resumeStep,
  setupChecklist,
} from "@/lib/project-setup";
import type { ProjectSetupStep, ProjectTrackerConfig, TrackerConnection } from "@/lib/types";
import { FolderStep } from "./FolderStep";
import { MachineStep } from "./MachineStep";
import { ReviewStep } from "./ReviewStep";
import { SetupStepper } from "./SetupStepper";
import { SourceStep } from "./SourceStep";
import { TeamStep } from "./TeamStep";

/**
 * The guided setup of one project (`/p/{slug}/setup?step=…`): folder & repos → machine check
 * → ticket source (with the dry run) → team & model → review & Start. Every step reuses the
 * settings page's own pieces and endpoints, so nothing set here differs from setting it later.
 * Leaving is fine: the URL names the step, the confirmed steps are saved, and an unnamed visit
 * resumes at the first step not confirmed. The ticket source is not polled until Start.
 */
export function ProjectSetup({ projectId }: { projectId: string }) {
  const t = useTranslations("dashboard.projectSetup");
  const withToken = useClientApi();
  const router = useRouter();
  const searchParams = useSearchParams();
  const updateSearch = useUpdateSearchParams();

  const { data, error, reload } = useApiQuery(async () => {
    const project = await api.projects.get(projectId, "");
    // Each part degrades on its own: a failed read is an empty panel, never a broken setup.
    const [repos, trackerConfig, connections, aiProvider, runConfig] = await Promise.all([
      api.repos.list(project.id, "").catch(() => []),
      api.trackers.getProjectConfig(project.id, "").catch(() => null),
      api.trackers.connections("").catch(() => []),
      api.org.aiProvider("").catch(() => null),
      api.runConfig.get(project.id, "").catch(() => null),
    ]);
    return { project, repos, trackerConfig, connections, aiProvider, runConfig };
  }, [projectId]);

  // The machine's answers, asked again by "Re-check" (and after a repository change).
  const [recheck, setRecheck] = useState(0);
  const machine = useApiQuery(async () => {
    const [runner, access, commands] = await Promise.all([
      api.runner.status("").catch(() => null),
      api.repos.accessCheck(projectId, "").catch(() => null),
      api.projects.setupCommands(projectId, "").catch(() => null),
    ]);
    return { runner, access, commands };
  }, [projectId, recheck]);

  // Local edits made in this visit, before the next reload brings them back from the API.
  const [configOverride, setConfigOverride] = useState<{ value: ProjectTrackerConfig | null } | null>(null);
  const [addedConnections, setAddedConnections] = useState<TrackerConnection[]>([]);
  const [previewed, setPreviewed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [stepError, setStepError] = useState<string | null>(null);
  const [startError, setStartError] = useState<string | null>(null);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <DashboardSkeleton />;

  const { project, repos, aiProvider, runConfig } = data;
  const trackerConfig = configOverride ? configOverride.value : data.trackerConfig;
  const connections = [
    ...data.connections,
    ...addedConnections.filter((c) => !data.connections.some((d) => d.id === c.id)),
  ];
  const step: ProjectSetupStep = parseStep(searchParams.get("step")) ?? resumeStep(project);
  const started = !!project.setupCompletedAt;
  const confirmed = project.setupSteps ?? [];
  const runner = machine.data?.runner ?? null;
  const checks = setupChecklist({
    project,
    repos,
    runner,
    access: machine.data?.access ?? null,
    commands: machine.data?.commands ?? null,
    trackerConfig,
    aiProvider,
    previewed,
  });

  const goTo = (next: ProjectSetupStep) => updateSearch((params) => params.set("step", next));

  function refresh() {
    invalidateApiCache();
    refreshData();
  }

  async function confirmAndContinue() {
    if (busy || step === "review") return;
    setBusy(true);
    setStepError(null);
    try {
      await withToken((token) => api.projects.setSetupStep(project.id, step, true, token));
      refresh();
      goTo(SETUP_STEPS[SETUP_STEPS.indexOf(step) + 1] ?? "review");
    } catch (e) {
      setStepError(extractErrorMessage(e, t("stepFailed")));
    } finally {
      setBusy(false);
    }
  }

  async function start(warnings: string[]) {
    setBusy(true);
    setStartError(null);
    try {
      await withToken((token) => api.projects.startSetup(project.id, warnings, token));
      refresh();
      router.push(`/p/${project.slug}/board`);
    } catch (e) {
      setStartError(extractErrorMessage(e, t("review.startFailed")));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto w-full max-w-4xl space-y-5 p-4 lg:p-8" data-testid="project-setup">
      <header>
        <h1 className="font-display text-2xl font-bold text-white">{t("title", { name: project.name })}</h1>
        <p className="mt-1 text-sm text-gray-400">{started ? t("subtitleStarted") : t("subtitle")}</p>
      </header>

      <SetupStepper current={step} confirmed={confirmed} checks={checks} onSelect={goTo} />

      <section aria-labelledby="setup-step-title" className="space-y-4">
        <div>
          <h2 id="setup-step-title" className="font-display text-lg font-semibold text-white">
            {t(`steps.${step}.title`)}
          </h2>
          <p className="mt-1 text-sm text-gray-400">{t(`steps.${step}.description`)}</p>
        </div>

        {step === "folder" && (
          <FolderStep
            projectId={project.id}
            project={project}
            repos={repos}
            commands={machine.data?.commands ?? null}
            runConfig={runConfig}
            onChanged={() => {
              refresh();
              setRecheck((n) => n + 1);
            }}
          />
        )}
        {step === "machine" && (
          <MachineStep
            runner={runner}
            access={machine.data?.access ?? null}
            repos={repos}
            rechecking={machine.loading}
            onRecheck={() => setRecheck((n) => n + 1)}
          />
        )}
        {step === "source" && (
          <SourceStep
            project={project}
            repos={repos}
            connections={connections}
            config={trackerConfig}
            previewed={previewed}
            onConfigChanged={(config) => {
              setConfigOverride({ value: config });
              setPreviewed(false);
            }}
            onConnectionAdded={(connection) => setAddedConnections((prev) => [...prev, connection])}
            onPreviewed={() => setPreviewed(true)}
          />
        )}
        {step === "team" && <TeamStep project={project} runner={runner} aiProvider={aiProvider} />}
        {step === "review" && (
          <ReviewStep
            checks={checks}
            started={started}
            starting={busy}
            startError={startError}
            onGoto={goTo}
            onStart={(warnings) => void start(warnings)}
          />
        )}
      </section>

      {step !== "review" && (
        <footer className="flex flex-wrap items-center justify-between gap-3 border-t border-white/[0.06] pt-4">
          <Button
            variant="ghost"
            onClick={() => goTo(SETUP_STEPS[Math.max(0, SETUP_STEPS.indexOf(step) - 1)])}
            disabled={step === SETUP_STEPS[0]}
          >
            {t("back")}
          </Button>
          <div className="flex items-center gap-3">
            {stepError && <span className="text-xs text-red-400">{stepError}</span>}
            {CONFIRMABLE_STEPS.includes(step) && (
              <Button onClick={() => void confirmAndContinue()} disabled={busy} data-testid="setup-continue">
                {confirmed.includes(step) ? t("continueAgain") : t("continue")}
              </Button>
            )}
          </div>
        </footer>
      )}
    </div>
  );
}
