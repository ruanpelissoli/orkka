import { useEffect } from "react";
import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { TaskDetailLive } from "@/components/task/TaskDetailLive";
import { OpenInEditorButton } from "@/components/task/OpenInEditorButton";
import { SetupFailureNotice } from "@/components/task/SetupFailureNotice";
import { InteractiveSessionBanner } from "@/components/task/InteractiveSessionBanner";
import { InteractiveApprovalPanel } from "@/components/task/InteractiveApprovalPanel";
import { DashboardSkeleton, TaskNotFound } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { getTaskByRef, isTaskNumber } from "@/lib/task-route";
import type {
  Milestone,
  PendingApproval,
  ProjectAgent,
  ProjectRepo,
  TaskAttachment,
  TaskExecution,
  TaskMessage,
  TaskPlan,
} from "@/lib/types";

export default function TaskDetailPage() {
  const { projectId, taskId } = useParams<{ projectId: string; taskId: string }>();
  const t = useTranslations("dashboard");
  const tc = useTranslations("common");
  const router = useRouter();
  useDocumentTitle(t("meta.taskDetail"));

  const { data } = useApiQuery(async () => {
    const task = await getTaskByRef(projectId, taskId);
    let messages: TaskMessage[] = [];
    let plan: TaskPlan | null = null;
    let attachments: TaskAttachment[] = [];
    let executions: TaskExecution[] = [];
    let approvals: PendingApproval[] = [];
    let milestones: Milestone[] = [];
    let repos: ProjectRepo[] = [];
    let agents: ProjectAgent[] = [];

    if (task) {
      // Fetch all related data in parallel
      const [messagesResult, planResult, attachmentsResult, executionsResult,
             milestonesResult, reposResult, agentsResult] =
        await Promise.allSettled([
          api.messages.list(task.id, ""),
          api.plans.latest(task.id, ""),
          api.attachments.list(task.id, ""),
          api.executions.list(task.id, ""),
          // Drive the detail header's pickers. A task can arrive with no repo (planner-created,
          // or the repo was unlinked) and cannot be submitted until it has one.
          api.milestones.list(projectId, ""),
          api.repos.list(projectId, ""),
          api.agents.list(projectId, ""),
        ]);

      if (milestonesResult.status === "fulfilled") milestones = milestonesResult.value.milestones;
      if (reposResult.status === "fulfilled") repos = reposResult.value;
      if (agentsResult.status === "fulfilled") agents = agentsResult.value;

      if (messagesResult.status === "fulfilled") messages = messagesResult.value;
      if (planResult.status === "fulfilled") plan = planResult.value;
      if (attachmentsResult.status === "fulfilled") attachments = attachmentsResult.value;
      if (executionsResult.status === "fulfilled") executions = executionsResult.value;

      // Only a live session can have a paused command. Fetched with the page so a reopened tab
      // renders the prompt immediately instead of waiting for the next live event.
      if (task.isInteractive && task.interactiveSessionStartedAt) {
        try {
          approvals = await api.tasks.listApprovals(task.id, "");
        } catch {
          // A failed fetch costs the initial render, not the feature — the SignalR handler
          // still delivers anything raised from here on.
        }
      }
    }
    return { task, messages, plan, attachments, executions, approvals, milestones, repos, agents };
  }, [projectId, taskId]);

  const task = data?.task ?? null;

  // Canonicalize legacy ULID URLs to the friendly number (only once the loaded task IS that ULID).
  const canonical =
    task && !isTaskNumber(taskId) && task.id.toUpperCase() === taskId.toUpperCase()
      ? `/p/${projectId}/tasks/${task.number}`
      : null;
  useEffect(() => {
    if (canonical) router.replace(canonical);
  }, [canonical, router]);

  if (!data || canonical) return <DashboardSkeleton />;

  if (!task) return <TaskNotFound projectId={projectId} taskId={taskId} />;

  return (
    <div className="mx-auto max-w-5xl p-4 lg:p-8">
      {/* Breadcrumb */}
      <div className="mb-4 flex items-center gap-2 text-xs text-gray-400">
        <Link
          href={`/p/${projectId}/board`}
          className="hover:text-gray-300 transition-colors"
        >
          {tc("nav.board")}
        </Link>
        <span>/</span>
        <span className="font-mono text-gray-400">{task.code}</span>
        <span>/</span>
        <span className="text-gray-400 truncate max-w-[200px]">{task.title}</span>
      </div>

      {/* Both sit above the task detail, where someone about to open the workspace looks:
          why it won't run, and the way in. Each renders nothing when not applicable. */}
      <SetupFailureNotice reason={task.setupFailure} className="mb-4" />
      <OpenInEditorButton taskId={task.id} className="mb-4" />

      {/* Renders only while a session is live — everything below already works for one
          unchanged, so the banner carries the single new affordance: ending it. */}
      <InteractiveSessionBanner task={task} className="mb-4" />

      <InteractiveApprovalPanel
        taskId={task.id}
        initialApprovals={data.approvals}
        className="mb-4"
      />

      <TaskDetailLive
        task={task}
        projectId={projectId}
        messages={data.messages}
        plan={data.plan}
        attachments={data.attachments}
        executions={data.executions}
        milestones={data.milestones}
        repos={data.repos}
        agents={data.agents}
      />
    </div>
  );
}
