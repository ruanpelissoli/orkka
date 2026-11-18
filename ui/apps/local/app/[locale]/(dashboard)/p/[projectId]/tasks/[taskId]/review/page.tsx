import { useEffect } from "react";
import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { GlassCard } from "@/components/ui";
import { EvidencePanel } from "@/components/task/EvidencePanel";
import { DashboardSkeleton, TaskNotFound } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { getTaskByRef, isTaskNumber } from "@/lib/task-route";
import { PrReviewLive } from "./PrReviewLive";
import { RequestHumanReviewButton } from "./RequestHumanReviewButton";

export default function PrReviewPage() {
  const { projectId, taskId } = useParams<{ projectId: string; taskId: string }>();
  const t = useTranslations("dashboard");
  const tc = useTranslations("common");
  const router = useRouter();
  useDocumentTitle(t("meta.reviewPr"));

  const { data } = useApiQuery(async () => {
    const task = await getTaskByRef(projectId, taskId);
    // A remote repo's reviewed work is published as a forge pull request rather than merged.
    // Unknown (the repo list failed) reads as local: merge wording, which is never wrong about
    // what Orkka does on the developer's machine.
    let repoIsRemote = false;
    if (task) {
      try {
        const repos = await api.repos.list(task.projectId, "");
        const repo = repos.find((r) => r.id === task.repoId);
        repoIsRemote = !!repo && repo.provider !== "local";
      } catch {
        // keep the default
      }
    }
    return { task, repoIsRemote };
  }, [projectId, taskId]);
  const task = data?.task ?? null;

  // Canonicalize legacy ULID URLs to the friendly number
  const canonical =
    task && !isTaskNumber(taskId) && task.id.toUpperCase() === taskId.toUpperCase()
      ? `/p/${projectId}/tasks/${task.number}/review`
      : null;
  useEffect(() => {
    if (canonical) router.replace(canonical);
  }, [canonical, router]);

  if (!data || canonical) return <DashboardSkeleton />;

  if (!task) return <TaskNotFound projectId={projectId} taskId={taskId} className="max-w-6xl" />;

  const { repoIsRemote } = data;

  if (task.currentLane !== "CodeReview" || !task.prUrl) {
    return (
      <div className="mx-auto max-w-6xl p-4 lg:p-8">
        <div className="mb-4 flex items-center gap-2 text-xs text-gray-400">
          <Link href={`/p/${projectId}/board`} className="hover:text-gray-300 transition-colors">
            {tc("nav.board")}
          </Link>
          <span>/</span>
          <Link
            href={`/p/${projectId}/tasks/${taskId}`}
            className="hover:text-gray-300 transition-colors truncate max-w-[200px]"
          >
            {task.title}
          </Link>
          <span>/</span>
          <span className="text-gray-400">{t("reviewPage.breadcrumb")}</span>
        </div>
        <GlassCard padding="lg">
          <div className="text-center py-8">
            {task.prUrl ? (
              // The work exists but the task has left Code Review — the reviewer asked for
              // changes and the developer is on them. Approve/merge reopen when it returns.
              <>
                <h2 className="font-display text-lg font-semibold text-white">
                  {t("reviewPage.notOpenTitle")}
                </h2>
                <p className="mt-2 text-sm text-gray-400">
                  {t("reviewPage.notOpenDescription")}
                </p>
                <RequestHumanReviewButton
                  taskId={task.id}
                  status={task.status}
                />
              </>
            ) : (
              <>
                <h2 className="font-display text-lg font-semibold text-white">
                  {t("reviewPage.nothingTitle")}
                </h2>
                <p className="mt-2 text-sm text-gray-400">
                  {t("reviewPage.nothingDescription")}
                </p>
              </>
            )}
            <Link
              href={`/p/${projectId}/tasks/${taskId}`}
              className="mt-4 inline-block text-sm text-primary-400 hover:text-primary-300"
            >
              {t("reviewPage.backToTask")}
            </Link>
          </div>
        </GlassCard>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-6xl p-4 lg:p-8">
      {/* Breadcrumb */}
      <div className="mb-4 flex items-center gap-2 text-xs text-gray-400">
        <Link href={`/p/${projectId}/board`} className="hover:text-gray-300 transition-colors">
          {tc("nav.board")}
        </Link>
        <span>/</span>
        <Link
          href={`/p/${projectId}/tasks/${taskId}`}
          className="hover:text-gray-300 transition-colors truncate max-w-[200px]"
        >
          {task.title}
        </Link>
        <span>/</span>
        <span className="text-gray-400">{t("meta.reviewPr")}</span>
      </div>

      {/* Evidence before diff: the reviewer should know what was promised and what QA
          concluded before spending attention on line-by-line reading. */}
      <EvidencePanel taskId={task.id} className="mb-4" />

      <PrReviewLive task={task} projectId={projectId} repoIsRemote={repoIsRemote} />
    </div>
  );
}
