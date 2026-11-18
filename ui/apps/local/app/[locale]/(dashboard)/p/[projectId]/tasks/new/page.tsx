import { useParams, useSearchParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { TaskCreateFormWithModels } from "./TaskCreateFormWithModels";
import { api } from "@/lib/api";
import type { AiModel, Milestone, ProjectAgent, ProjectRepo } from "@/lib/types";

export default function NewTaskPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const searchParams = useSearchParams();
  const from = searchParams.get("from");
  const milestoneId = searchParams.get("milestoneId") ?? undefined;
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.newTask"));
  // Whitelisted origins only — after creation the form returns the user here. A task added
  // from a milestone page goes back to that milestone, where the new task now shows.
  const returnTo =
    from === "tasks"
      ? `/p/${projectId}/tasks`
      : from === "milestone" && milestoneId
        ? `/p/${projectId}/roadmap/${milestoneId}`
        : `/p/${projectId}/board`;

  const { data } = useApiQuery(async () => {
    let repos: ProjectRepo[] = [];
    let agents: ProjectAgent[] = [];
    let models: AiModel[] = [];
    let milestones: Milestone[] = [];
    try {
      [repos, agents, models] = await Promise.all([
        api.repos.list(projectId, ""),
        api.agents.list(projectId, ""),
        api.models.list(""),
      ]);
    } catch {
      // API may not be available yet
    }
    try {
      milestones = (await api.milestones.list(projectId, "")).milestones;
    } catch {
      // Separate from the call above on purpose: a project with no roadmap still gets
      // a working create form, it just hides the milestone picker.
    }
    return { repos, agents, models, milestones };
  }, [projectId]);

  if (!data) return <DashboardSkeleton />;

  // The whole chat catalog goes down, runner-only rows included: whether a Copilot or Ollama
  // model is pickable depends on what THIS user's runner has signed in, which only the client
  // knows. Image models are picked per design agent, not per task.
  const chatModels = data.models.filter((m) => m.kind === "Chat");

  return (
    <div className="mx-auto w-full max-w-2xl p-4 lg:p-8 xl:max-w-[90rem]">
      <div className="mb-6">
        <h1 className="font-display text-2xl font-bold text-white">
          {t("taskCreateForm.createTask")}
        </h1>
        <p className="mt-1 text-sm text-gray-400">
          {t("uiMisc.newTaskSubtitle")}
        </p>
      </div>
      <TaskCreateFormWithModels
        projectId={projectId}
        repos={data.repos}
        agents={data.agents}
        catalog={chatModels}
        returnTo={returnTo}
        milestones={data.milestones}
        defaultMilestoneId={milestoneId}
      />
    </div>
  );
}
