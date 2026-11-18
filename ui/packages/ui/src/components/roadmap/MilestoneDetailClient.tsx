import { useCallback, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Button, GlassCard, buttonVariants } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { useClientApi } from "@/hooks/use-client-api";
import { useBoardSignalR } from "@/hooks/use-board-signalr";
import { useMilestonePlan } from "@/hooks/use-milestone-plan";
import { api } from "@/lib/api";
import { cn } from "@/lib/utils";
import type { MilestoneDetail, MilestoneStatusKey } from "@/lib/types";
import { MilestonePlanReview } from "./MilestonePlanReview";
import { MilestonePlanProgress } from "./MilestonePlanProgress";

interface MilestoneDetailClientProps {
  /** Route ref used in URLs — the project slug (or a legacy ULID). */
  projectId: string;
  initial: MilestoneDetail;
}

const STATUS_TONE: Record<MilestoneStatusKey, string> = {
  Planned: "text-gray-400 bg-white/[0.04] border-white/[0.06]",
  Active: "text-primary-300 bg-primary-500/10 border-primary-500/20",
  Completed: "text-emerald-300 bg-emerald-500/10 border-emerald-500/20",
  Cancelled: "text-gray-400 bg-white/[0.02] border-white/[0.04]",
};

/**
 * One milestone and the tasks under it, with the on-demand planner.
 *
 * This is where the roadmap stops being a list of intentions: milestones are seeded empty from
 * the brief, and their tasks are planned here, when someone asks for them. Nothing is created
 * until the proposal is accepted — each task is an agent run that costs real money.
 */
export function MilestoneDetailClient({ projectId, initial }: MilestoneDetailClientProps) {
  const t = useTranslations("dashboard");
  const withToken = useClientApi();

  const [detail, setDetail] = useState<MilestoneDetail>(initial);
  const [busy, setBusy] = useState(false);

  const { milestone, tasks, previousUnfinished } = detail;

  // Planning this milestone's tasks is a multi-minute run that lives on the server. Opening
  // this page reattaches to one already in progress rather than offering to start a second.
  const {
    proposal,
    setProposal,
    planning,
    step,
    elapsedSeconds,
    failureCode,
    error: planError,
    start,
    reset: resetPlan,
    onPlanChanged,
  } = useMilestonePlan({ projectId, milestoneId: milestone.id });

  const reload = useCallback(async () => {
    const next = await withToken((token) =>
      api.milestones.get(projectId, milestone.id, token)
    );
    setDetail(next);
  }, [withToken, projectId, milestone.id]);

  // Live task list. This page used to be render-once: tasks moved lanes on the board while
  // the milestone still showed them where they were at page load. Task events don't carry a
  // milestone id, so any of them may concern this list — coalesce the refetches instead of
  // trying to filter.
  const reloadTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reloadSoon = useCallback(() => {
    if (reloadTimer.current) return;
    reloadTimer.current = setTimeout(() => {
      reloadTimer.current = null;
      void reload();
    }, 400);
  }, [reload]);

  const handleMilestoneChanged = useCallback(
    (event: { milestoneId: string }) => {
      if (event.milestoneId === milestone.id) reloadSoon();
    },
    [milestone.id, reloadSoon]
  );

  useBoardSignalR({
    projectId: milestone.projectId,
    onLaneChanged: reloadSoon,
    onStatusChanged: reloadSoon,
    onTaskCreated: reloadSoon,
    onTaskDeleted: reloadSoon,
    onTaskUpdated: reloadSoon,
    onMilestoneChanged: handleMilestoneChanged,
    onMilestonePlanChanged: onPlanChanged,
  });

  // No goal is sent: the milestone's own outcome is the goal, and the server derives it. A
  // second copy in a textarea here would be free to drift from the outcome on the card above.
  const plan = useCallback(() => {
    void start();
  }, [start]);

  // An offline runner is "start your runner", not a failure to plan: planning runs there, on the
  // developer's own AI subscription, and the job's failure code says which it was.
  const planErrorMessage =
    planError === null
      ? null
      : failureCode === "runner_offline"
        ? t("roadmapPage.planRunnerOffline")
        : failureCode === "unusable"
          ? t("roadmapPage.planRetry")
          : t("roadmapPage.planFailed");

  const acceptPlan = useCallback(async () => {
    if (!proposal || proposal.tasks.length === 0) return;
    setBusy(true);
    try {
      await withToken((token) =>
        api.milestones.accept(
          projectId,
          {
            // Sent for the shape of the request; the server keeps the stored title and outcome
            // when milestoneId is set.
            milestoneTitle: milestone.title,
            outcome: milestone.outcome ?? undefined,
            tasks: proposal.tasks,
            milestoneId: milestone.id,
            repoId: proposal.repoId ?? undefined,
            // Without this the halves of a cross-repo task are created as unrelated tasks:
            // no shared contract, no merge gate.
            contracts: proposal.contracts ?? undefined,
          },
          token
        )
      );
      resetPlan();
      await reload();
    } finally {
      setBusy(false);
    }
  }, [proposal, withToken, projectId, milestone, reload, resetPlan]);

  return (
    <div className="space-y-4">
      <GlassCard className="p-5">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="font-display text-lg font-bold text-white">{milestone.title}</h2>
              <span
                className={cn(
                  "rounded-full border px-2 py-0.5 text-label",
                  STATUS_TONE[milestone.status]
                )}
              >
                {t(`roadmapPage.status.${milestone.status}`)}
              </span>
              {/* Which repo this milestone's tasks land in — and therefore which tree the
                  planner reads when generating them. */}
              {milestone.repoName && (
                <span className="rounded-full border border-white/[0.06] bg-white/[0.04] px-2 py-0.5 font-mono text-xs text-gray-400">
                  {milestone.repoName}
                </span>
              )}
            </div>
            <p
              className={cn(
                "mt-1 text-sm",
                milestone.outcome ? "text-gray-400" : "italic text-gray-400"
              )}
            >
              {milestone.outcome ?? t("roadmapPage.noOutcome")}
            </p>
          </div>

          <Link
            href={`/p/${projectId}/roadmap`}
            className="shrink-0 font-mono text-xs text-primary-400 hover:text-primary-300"
          >
            ← {t("roadmapPage.title")}
          </Link>
        </div>

        {/* A hint, never a block. Planning ahead is the user's call — the platform already
            refuses to auto-create tasks when a milestone completes for the same reason. */}
        {previousUnfinished && (
          <p className="mt-4 rounded-lg border border-amber-500/20 bg-amber-500/[0.06] p-3 text-xs text-amber-300/90">
            {t("milestonePage.previousUnfinished", { title: previousUnfinished.title })}
          </p>
        )}
      </GlassCard>

      {proposal !== null ? (
        <MilestonePlanReview
          proposal={proposal}
          onChange={setProposal}
          onAccept={acceptPlan}
          onDiscard={resetPlan}
          busy={busy}
          intoExistingMilestone
        />
      ) : (
        <GlassCard className="p-5">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="min-w-0">
              <h3 className="font-display text-base font-bold text-white">
                {t("milestonePage.tasksHeading", { count: tasks.length })}
              </h3>
              {tasks.length === 0 && (
                <p className="mt-1 text-sm text-gray-400">{t("milestonePage.noTasksHint")}</p>
              )}
            </div>

            <RequirePermission permission="TasksCreate">
              <div className="flex flex-wrap items-center gap-2">
                {/* The planner is the usual way tasks get here, but it can miss something the
                    user knows about. A hand-written task lands in this milestone through the
                    ordinary create form (preselected), so it runs and orders like the rest. */}
                <Link
                  href={`/p/${projectId}/tasks/new?from=milestone&milestoneId=${milestone.id}`}
                  className={buttonVariants({ variant: "secondary", size: "sm" })}
                >
                  {t("milestonePage.addTask")}
                </Link>
                <Button size="sm" onClick={plan} disabled={busy || planning}>
                  {planning
                    ? t("roadmapPage.planning")
                    : tasks.length === 0
                      ? t("milestonePage.generateTasks")
                      : t("milestonePage.planMoreTasks")}
                </Button>
              </div>
            </RequirePermission>
          </div>

          {/* Generating this milestone's tasks is the slowest thing on the page — minutes, not
              seconds. Saying which phase it is in, and that leaving is safe, is what keeps the
              user from killing it halfway through. */}
          {planning && step && (
            <div className="mt-4">
              <MilestonePlanProgress step={step} elapsedSeconds={elapsedSeconds} />
            </div>
          )}

          {planErrorMessage && (
            <p className="mt-3 text-xs text-amber-400">{planErrorMessage}</p>
          )}

          {tasks.length > 0 && (
            <ul className="mt-4 space-y-2">
              {tasks.map((task) => (
                <li key={task.id}>
                  {/* The per-project number is the canonical task URL; the route also accepts
                      a ULID, but only for legacy links. */}
                  <Link
                    href={`/p/${projectId}/tasks/${task.number}`}
                    className="flex items-start justify-between gap-3 rounded-lg border border-white/[0.06] bg-white/[0.02] p-3 hover:border-white/[0.12] hover:bg-white/[0.04]"
                  >
                    <div className="min-w-0">
                      <p className="truncate text-sm text-gray-200">
                        <span className="text-xs text-gray-400">#{task.number}</span>{" "}
                        {task.title}
                      </p>
                      <p className="mt-0.5 text-label">
                        {task.type} · {task.priority}
                      </p>
                    </div>
                    <span className="shrink-0 text-label">
                      {task.lane}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </GlassCard>
      )}
    </div>
  );
}
