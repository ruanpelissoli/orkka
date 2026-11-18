import { useParams } from "@/lib/navigation";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { GlassCard, Badge, Button, PriorityBadge } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { RepoProviderIcon } from "@/components/ui/brand-icons";
import { DashboardSkeleton, PageError } from "@/components/page";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";
import { LANE_KEYS } from "@/lib/constants";
import type { Task } from "@/lib/types";

export default function ProjectDashboardPage() {
  const { projectId } = useParams<{ projectId: string }>();
  const t = useTranslations("dashboard");
  useDocumentTitle(t("meta.projectOverview"));

  const { data, error, reload } = useApiQuery(async () => {
    // Started alongside the project fetch — the task list does not depend on it. The catch is
    // attached eagerly so that if the project fetch throws, this in-flight promise can never
    // surface as an unhandled rejection.
    const tasksPromise = api.tasks.list(projectId, "").catch((err) => {
      console.error("[overview page] failed to load tasks:", err);
      return null;
    });
    const project = await api.projects.get(projectId, "");
    return { project, loadedTasks: await tasksPromise };
  }, [projectId]);

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <DashboardSkeleton />;

  const { project, loadedTasks } = data;
  const tasks: Task[] = loadedTasks ?? [];
  const tasksError: string | null =
    loadedTasks === null ? t("projectOverview.failedToLoadTasks") : null;

  const activeTasks = tasks.filter(
    (t) => ![LANE_KEYS.DONE, LANE_KEYS.DRAFTING].includes(t.currentLane)
  ).length;
  const inReview = tasks.filter((t) => t.currentLane === LANE_KEYS.CODE_REVIEW).length;
  const completed = tasks.filter((t) => t.currentLane === LANE_KEYS.DONE).length;
  const activeAgents = tasks.filter(
    (t) => t.currentLane === LANE_KEYS.PLANNING || t.currentLane === LANE_KEYS.DEVELOPING
  ).length;

  const stats = [
    { label: t("projectOverview.activeTasks"), value: String(activeTasks), color: "#3b82f6" },
    { label: t("projectOverview.inReview"), value: String(inReview), color: "#06b6d4" },
    { label: t("projectOverview.completed"), value: String(completed), color: "#10b981" },
    { label: t("projectOverview.activeAgents"), value: String(activeAgents), color: "#8b5cf6" },
  ];

  const recentTasks = tasks.slice(0, 5);

  return (
    <div className="mx-auto max-w-7xl space-y-8 p-4 lg:p-8">
      {/* Welcome */}
      <div className="flex items-start justify-between">
        <div>
          <h1 className="font-display text-2xl font-bold text-white">
            {project.name}
          </h1>
          <p className="mt-1 text-gray-400">
            {project.description || t("projectOverview.defaultDescription")}
          </p>
        </div>
      </div>

      {/* Quick stats */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {stats.map((stat) => (
          <GlassCard key={stat.label} padding="md" hover>
            <div className="flex items-center justify-between">
              <span className="text-sm text-gray-400">{stat.label}</span>
              <span
                className="h-2 w-2 rounded-full"
                style={{
                  backgroundColor: stat.color,
                  boxShadow: `0 0 8px ${stat.color}60`,
                }}
              />
            </div>
            <p className="mt-2 font-display text-3xl font-bold text-white">
              {stat.value}
            </p>
          </GlassCard>
        ))}
      </div>

      <div className="grid gap-6 lg:grid-cols-3">
        {/* Recent Tasks */}
        <GlassCard padding="none" className="lg:col-span-2">
          <div className="flex items-center justify-between border-b border-white/[0.04] px-5 py-4">
            <h2 className="font-display text-base font-semibold text-white">
              {t("projectOverview.recentTasks")}
            </h2>
            <Link href={`/p/${projectId}/board`}>
              <Badge variant="primary" size="sm" className="cursor-pointer">
                {t("projectOverview.viewBoard")}
              </Badge>
            </Link>
          </div>
          {tasksError ? (
            <div className="px-5 py-8 text-center text-sm text-red-400">
              {tasksError}
            </div>
          ) : recentTasks.length > 0 ? (
            <div className="divide-y divide-white/[0.04]">
              {recentTasks.map((task) => (
                <Link
                  key={task.id}
                  href={`/p/${projectId}/tasks/${task.number}`}
                  className="flex items-center justify-between px-5 py-3 transition-colors hover:bg-white/[0.02]"
                >
                  <div className="min-w-0 flex-1">
                    <p className="text-sm font-medium text-white truncate">
                      <span className="mr-2 font-mono text-xs text-gray-400">{task.code}</span>
                      {task.title}
                    </p>
                    <div className="flex items-center gap-2 mt-0.5">
                      <span className="text-xs text-gray-400">{task.currentLane}</span>
                      {task.repoName && (
                        <span className="text-xs text-gray-400 font-mono truncate">
                          {task.repoName}
                        </span>
                      )}
                    </div>
                  </div>
                  <PriorityBadge priority={task.priority} size="sm" />
                </Link>
              ))}
            </div>
          ) : (
            <div className="px-5 py-8 text-center text-sm text-gray-400">
              {t("projectOverview.noTasksYet")}
            </div>
          )}
        </GlassCard>

        {/* Quick Actions */}
        <GlassCard padding="md">
          <h2 className="font-display text-base font-semibold text-white mb-4">
            {t("projectOverview.quickActions")}
          </h2>
          <div className="space-y-2">
            <RequirePermission permission="TasksCreate">
              <Link href={`/p/${projectId}/tasks/new`} className="block">
                <Button variant="secondary" className="w-full justify-start gap-3" size="md">
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
                    <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
                  </svg>
                  {t("projectOverview.newTask")}
                </Button>
              </Link>
            </RequirePermission>
            <Link href={`/p/${projectId}/repos`} className="block">
              <Button variant="ghost" className="w-full justify-start gap-3" size="md">
                <RepoProviderIcon provider="other" size={16} />
                {t("projectOverview.manageRepos")}
              </Button>
            </Link>
            <Link href={`/p/${projectId}/settings`} className="block">
              <Button variant="ghost" className="w-full justify-start gap-3" size="md">
                <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
                  <path d="M12.22 2h-.44a2 2 0 00-2 2v.18a2 2 0 01-1 1.73l-.43.25a2 2 0 01-2 0l-.15-.08a2 2 0 00-2.73.73l-.22.38a2 2 0 00.73 2.73l.15.1a2 2 0 011 1.72v.51a2 2 0 01-1 1.74l-.15.09a2 2 0 00-.73 2.73l.22.38a2 2 0 002.73.73l.15-.08a2 2 0 012 0l.43.25a2 2 0 011 1.73V20a2 2 0 002 2h.44a2 2 0 002-2v-.18a2 2 0 011-1.73l.43-.25a2 2 0 012 0l.15.08a2 2 0 002.73-.73l.22-.39a2 2 0 00-.73-2.73l-.15-.08a2 2 0 01-1-1.74v-.5a2 2 0 011-1.74l.15-.09a2 2 0 00.73-2.73l-.22-.38a2 2 0 00-2.73-.73l-.15.08a2 2 0 01-2 0l-.43-.25a2 2 0 01-1-1.73V4a2 2 0 00-2-2z" strokeLinecap="round" strokeLinejoin="round" />
                  <circle cx="12" cy="12" r="3" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
                {t("projectOverview.projectSettings")}
              </Button>
            </Link>
          </div>
        </GlassCard>
      </div>
    </div>
  );
}
