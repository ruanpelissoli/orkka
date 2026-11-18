import { Suspense } from "react";
import { useParams, useSearchParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { BoardViewSwitch } from "@/components/board";
import { Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { TaskFilterBar } from "@/components/task/TaskFilterBar";
import { TaskList } from "@/components/task/TaskList";
import { DashboardSkeleton } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import type { Task, ProjectRepo, TaskFilterParams } from "@/lib/types";

export default function TasksPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const sp = Object.fromEntries(useSearchParams().entries()) as Record<string, string | undefined>;
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.tasks"));

  const filterParams: TaskFilterParams = {
    search: sp.search || undefined,
    repoId: sp.repoId || undefined,
    status: sp.status || undefined,
    isArchived: sp.isArchived === "true" ? true : undefined,
    page: sp.page ? parseInt(sp.page, 10) : 1,
    pageSize: 50,
  };

  const { data } = useApiQuery(async () => {
    let tasks: Task[] = [];
    let repos: ProjectRepo[] = [];
    try {
      [tasks, repos] = await Promise.all([
        api.tasks.listFiltered(projectId, filterParams, ""),
        api.repos.list(projectId, ""),
      ]);
    } catch {
      // API may not be available yet
    }
    return { tasks, repos };
  }, [projectId, filterParams]);

  if (!data) return <DashboardSkeleton />;

  const { tasks, repos } = data;

  const currentPage = filterParams.page ?? 1;
  const hasMore = tasks.length === (filterParams.pageSize ?? 50);

  return (
    <div className="mx-auto max-w-5xl p-4 lg:p-8">
      {/* Header */}
      <div className="mb-6 flex items-center justify-between">
        <div className="flex items-center gap-4">
          <h2 className="font-display text-lg font-bold text-white">{t("tasksPage.tasks")}</h2>
          <BoardViewSwitch projectId={projectId} active="list" />
        </div>
        <RequirePermission permission="TasksCreate">
          <Link href={`/p/${projectId}/tasks/new?from=tasks`}>
            <Button size="sm">
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M8 3v10M3 8h10" strokeLinecap="round" />
              </svg>
              {t("tasksPage.newTask")}
            </Button>
          </Link>
        </RequirePermission>
      </div>

      {/* Filters */}
      <div className="mb-4">
        <Suspense>
          <TaskFilterBar repos={repos} projectId={projectId} />
        </Suspense>
      </div>

      {/* Task list */}
      <TaskList tasks={tasks} projectId={projectId} />

      {/* Pagination */}
      {(currentPage > 1 || hasMore) && (
        <div className="mt-4 flex items-center justify-between">
          {currentPage > 1 ? (
            <Link
              href={`/p/${projectId}/tasks?${new URLSearchParams({
                ...sp as Record<string, string>,
                page: String(currentPage - 1),
              }).toString()}`}
              className="text-xs text-gray-400 hover:text-gray-200 transition-colors"
            >
              {t("tasksPage.previous")}
            </Link>
          ) : (
            <span />
          )}
          <span className="text-xs text-gray-400">
            {t("tasksPage.page", { number: currentPage })}
          </span>
          {hasMore ? (
            <Link
              href={`/p/${projectId}/tasks?${new URLSearchParams({
                ...sp as Record<string, string>,
                page: String(currentPage + 1),
              }).toString()}`}
              className="text-xs text-gray-400 hover:text-gray-200 transition-colors"
            >
              {t("tasksPage.next")}
            </Link>
          ) : (
            <span />
          )}
        </div>
      )}
    </div>
  );
}
