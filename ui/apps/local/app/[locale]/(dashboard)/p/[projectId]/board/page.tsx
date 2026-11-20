import { useParams, useSearchParams, useUpdateSearchParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { TaskBoard, AutopilotToggle, BoardViewSwitch, TicketSourceStatusLine, ImportTicketButton } from "@/components/board";
import { CodeView } from "@/components/code-view";
import { Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { StartSessionButton } from "@/components/task/StartSessionButton";
import { ProviderMismatchBanner } from "@/components/runner/ProviderMismatchBanner";
import { FinishSetupBanner } from "@/components/project-setup/FinishSetupBanner";
import { BoardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import type {
  TaskBoardResponse,
  MilestoneListResponse,
  AutopilotStatus,
  BoardMetricsResponse,
  ProjectAgent,
  ProjectRepo,
} from "@/lib/types";

export default function BoardPage() {
  const { projectId } = useParams<{ projectId: string }>();
  // The kanban Board is the default; `?view=code` selects the Code View workbench.
  // `?task=` is shared by both views, so switching keeps your place.
  const searchParams = useSearchParams();
  const view = searchParams.get("view");
  const taskParam = searchParams.get("task");
  const showKanban = view !== "code";
  const updateSearchParams = useUpdateSearchParams();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.board"));

  const { data } = useApiQuery(
    () =>
      // All take the raw route param, so none depends on another — issued together rather
      // than in sequence. allSettled keeps the per-call fallbacks below independent.
      Promise.allSettled([
        api.projects.get(projectId, ""),
        api.board.get(projectId, ""),
        api.milestones.list(projectId, ""),
        api.autopilot.get(projectId, ""),
        // Only to answer "which repo?" when starting a session — the button hides itself when
        // the project has none, so a failed fetch degrades to no button rather than an error.
        api.repos.list(projectId, ""),
        // Seeds the board strip so it has numbers on first paint rather than flashing a skeleton.
        api.board.metrics(projectId, "7d", ""),
        // Only for the provider-mismatch strip; a failed fetch means no strip, never a broken board.
        api.agents.list(projectId, ""),
        api.trackers.getProjectConfig(projectId, ""),
      ] as const),
    [projectId]
  );

  if (!data) return <BoardSkeleton />;

  const [
    projectResult,
    boardResult,
    milestonesResult,
    autopilotResult,
    reposResult,
    metricsResult,
    agentsResult,
    trackerResult,
  ] = data;

  // SignalR board groups are keyed by the project ULID, so resolve the project even
  // when the route param is a slug; fall back to the raw param (legacy ULID URLs)
  const projectUlid =
    projectResult.status === "fulfilled" ? projectResult.value.id : projectId;

  // Board API may not be available yet — show empty board
  const boardData: TaskBoardResponse =
    boardResult.status === "fulfilled" ? boardResult.value : { lanes: [] };

  // The filter simply doesn't render when there are no milestones, so an empty list
  // is the correct fallback rather than an error state.
  const milestones: MilestoneListResponse["milestones"] =
    milestonesResult.status === "fulfilled" ? milestonesResult.value.milestones : [];

  const repos: ProjectRepo[] = reposResult.status === "fulfilled" ? reposResult.value : [];
  const agents: ProjectAgent[] = agentsResult.status === "fulfilled" ? agentsResult.value : [];
  const ticketSource = trackerResult.status === "fulfilled" ? trackerResult.value : null;

  // An older API without the autopilot endpoint renders the toggle in its off state rather
  // than failing the board — same per-call fallback the other three use.
  const autopilot: AutopilotStatus =
    autopilotResult.status === "fulfilled"
      ? autopilotResult.value
      : {
          running: false,
          sessionId: null,
          startedAt: null,
          stoppedAt: null,
          stopReason: null,
          capUsd: null,
          spentUsd: 0,
          tasksSubmitted: 0,
          tasksCompleted: 0,
          tasksFailed: 0,
          tasksInFlight: 0,
          tasksWaitingOnYou: 0,
          draftingRemaining: 0,
        };

  // Metrics are context, not the page. A failure means no strip, never a broken board — the
  // client hook refetches on the next hub event anyway.
  const initialMetrics: BoardMetricsResponse | null =
    metricsResult.status === "fulfilled" ? metricsResult.value : null;

  const totalTasks = boardData.lanes.reduce((sum, lane) => sum + lane.tasks.length, 0);

  return (
    // Absolute against the shell's .noise wrapper (position: relative) — the only
    // way to get a definite viewport height, so lanes scroll internally and the
    // horizontal scrollbar stays visible without scrolling the page
    <div className="absolute inset-0 flex flex-col overflow-hidden py-4">
      {/* Board header */}
      <div className="mb-4 flex items-center justify-between px-4 lg:px-8">
        <div className="flex items-center gap-4">
          <h2 className="font-display text-lg font-bold text-white">
            {showKanban ? t("boardPage.board") : t("boardPage.code")}
          </h2>
          <BoardViewSwitch projectId={projectId} active={showKanban ? "board" : "code"} taskParam={taskParam} />
          {/* Every project of this workspace on one board. */}
          <Link
            href="/board"
            className="hidden text-xs text-gray-400 transition-colors hover:text-gray-200 sm:inline"
            data-testid="project-board-workspace-link"
          >
            {t("boardPage.workspaceBoard")}
          </Link>
        </div>
        <div className="flex items-center gap-3">
          {/* The kanban's own filter row carries "showing N of M", which says strictly more.
              The count stays in the header for Code View, which has no such row. */}
          {!showKanban && (
            <span className="text-xs text-gray-400">
              {t("boardPage.tasks", { count: totalTasks })}
            </span>
          )}
          <AutopilotToggle
            projectId={projectId}
            projectUlid={projectUlid}
            initialStatus={autopilot}
            automationPaused={projectResult.status === "fulfilled" && projectResult.value.settings?.automationEnabled === false}
          />
          <RequirePermission permission="TasksExecute">
            <StartSessionButton projectId={projectId} repos={repos} />
          </RequirePermission>
          {/* Start from a ticket's key in the project's tracker, whoever it is assigned to. */}
          {ticketSource && (
            <RequirePermission permission="TasksCreate">
              <ImportTicketButton
                projects={[{ slug: projectId, name: projectResult.status === "fulfilled" ? projectResult.value.name : projectId }]}
                onImported={(taskId) => updateSearchParams((p) => p.set("task", taskId))}
              />
            </RequirePermission>
          )}
          <RequirePermission permission="TasksCreate">
            <Link href={`/p/${projectId}/tasks/new?from=board`}>
              <Button size="sm">
                <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M8 3v10M3 8h10" strokeLinecap="round" />
                </svg>
                {t("boardPage.newTask")}
              </Button>
            </Link>
          </RequirePermission>
        </div>
      </div>

      {/* A project whose guided setup was left before Start pulls no tickets yet. */}
      {projectResult.status === "fulfilled" && (
        <FinishSetupBanner project={projectResult.value} className="-mt-2 mb-3 mx-4 lg:mx-8" />
      )}

      {/* Where the board's tickets come from, when a tracker feeds it. */}
      {ticketSource && (
        <div className="-mt-2 mb-3 px-4 lg:px-8">
          <TicketSourceStatusLine config={ticketSource} />
        </div>
      )}

      {/* Renders only when some agents' models come from a provider the user's runner has not
          signed in — their tasks would sit waiting, which is worth a line on the board. */}
      {agents.length > 0 && (
        <ProviderMismatchBanner
          agents={agents}
          projectId={projectId}
          compact
          className="mb-3 mx-4 lg:mx-8"
        />
      )}

      {showKanban ? (
        <TaskBoard
          boardData={boardData}
          projectId={projectId}
          projectUlid={projectUlid}
          milestones={milestones}
          initialMetrics={initialMetrics}
        />
      ) : (
        <CodeView
          boardData={boardData}
          projectId={projectId}
          projectUlid={projectUlid}
        />
      )}
    </div>
  );
}
