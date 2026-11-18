import { useEffect } from "react";
import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { MergeResolutionCenter } from "@/components/merge/MergeResolutionCenter";
import { DashboardSkeleton, TaskNotFound } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { getTaskByRef, isTaskNumber } from "@/lib/task-route";

export default function MergeResolutionPage() {
  const { projectId, taskId } = useParams<{ projectId: string; taskId: string }>();
  const t = useTranslations("dashboard");
  const tc = useTranslations("common");
  const router = useRouter();
  useDocumentTitle(t("meta.merge"));

  const { data } = useApiQuery(
    async () => ({ task: await getTaskByRef(projectId, taskId) }),
    [projectId, taskId]
  );
  const task = data?.task ?? null;

  // Canonicalize legacy ULID URLs to the friendly number
  const canonical =
    task && !isTaskNumber(taskId) && task.id.toUpperCase() === taskId.toUpperCase()
      ? `/p/${projectId}/tasks/${task.number}/merge`
      : null;
  useEffect(() => {
    if (canonical) router.replace(canonical);
  }, [canonical, router]);

  if (!data || canonical) return <DashboardSkeleton />;

  if (!task) return <TaskNotFound projectId={projectId} taskId={taskId} className="max-w-4xl" />;

  return (
    <div className="mx-auto max-w-4xl p-4 lg:p-8">
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
        <span className="text-gray-400">{t("meta.merge")}</span>
      </div>

      <MergeResolutionCenter taskId={task.id} projectId={projectId} taskNumber={task.number} />
    </div>
  );
}
