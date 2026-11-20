import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { Link, useRouter } from "@/i18n/navigation";
import { useSearchParams, useUpdateSearchParams } from "@/lib/navigation";
import { TaskBoard, ImportTicketButton } from "@/components/board";
import { Button, Dropdown, EmptyState, Select } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { BoardSkeleton, PageError } from "@/components/page";
import { useOptionalWorkspaces } from "@/components/workspace/WorkspaceProvider";
import { useApiQuery } from "@/hooks/use-api-query";
import { useDocumentTitle } from "@/hooks/use-document-title";
import { api } from "@/lib/api";

/** `?project=` — the project the workspace board is narrowed to (its slug; an id works too). */
const PROJECT_PARAM = "project";

const PLUS_ICON = (
  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
    <path d="M8 3v10M3 8h10" strokeLinecap="round" />
  </svg>
);

/**
 * The workspace board: every task of this workspace (one job) across all its projects — in
 * Orkka every task is the developer's own work, so this is everything assigned to them in this
 * workspace. The project filter narrows it to one project and lives in the URL (`?project=`), so
 * a narrowed board can be bookmarked and survives a reload. It is the landing page.
 */
export default function WorkspaceBoardPage() {
  const t = useTranslations("dashboard");
  const tFirstRun = useTranslations("dashboard.firstRun");
  useDocumentTitle(t("meta.workspaceBoard"));
  const router = useRouter();
  const searchParams = useSearchParams();
  const updateSearchParams = useUpdateSearchParams();
  const projectParam = searchParams.get(PROJECT_PARAM) ?? "";
  const workspaceName = useOptionalWorkspaces()?.current?.name ?? null;

  const { data, error, reload } = useApiQuery(async () => {
    const projects = await api.projects.list("");
    // A bookmark can outlive its project (or name another workspace's): the board then shows
    // every project and says so, rather than failing the page.
    const selected = projectParam
      ? (projects.find((p) => p.slug === projectParam || p.id === projectParam) ?? null)
      : null;
    const board = projects.length > 0 ? await api.board.workspace(selected?.slug ?? null, "") : { lanes: [] };
    return { projects, selected, board };
  }, [projectParam]);

  const projectUlids = useMemo(
    () => (data ? (data.selected ? [data.selected.id] : data.projects.map((p) => p.id)) : []),
    [data]
  );

  if (error) return <PageError error={error} onRetry={reload} />;
  if (!data) return <BoardSkeleton />;

  const { projects, selected, board } = data;
  const unknownProject = projectParam !== "" && selected === null;

  const setProject = (value: string) =>
    updateSearchParams((p) => {
      if (value) p.set(PROJECT_PARAM, value);
      else p.delete(PROJECT_PARAM);
      // An open task belongs to the board it was opened from.
      p.delete("task");
    });

  if (projects.length === 0) {
    return (
      <div className="flex flex-1 items-center justify-center p-4 lg:p-8" data-testid="workspace-board-empty">
        <EmptyState
          title={tFirstRun("title")}
          description={tFirstRun("description")}
          action={
            <Link href="/projects/new">
              <Button>{tFirstRun("createProject")}</Button>
            </Link>
          }
        />
      </div>
    );
  }

  return (
    // Absolute against the shell's .noise wrapper, like the project board: lanes scroll inside.
    <div className="absolute inset-0 flex flex-col overflow-hidden py-4" data-testid="workspace-board">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3 px-4 lg:px-8">
        <div className="min-w-0">
          <h2 className="font-display text-lg font-bold text-white">{t("workspaceBoard.title")}</h2>
          <p className="truncate text-xs text-gray-400">
            {workspaceName
              ? t("workspaceBoard.subtitleNamed", { workspace: workspaceName })
              : t("workspaceBoard.subtitle")}
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <Select
            size="sm"
            value={selected?.slug ?? ""}
            onChange={(e) => setProject(e.target.value)}
            aria-label={t("workspaceBoard.projectFilter")}
            className="max-w-[220px]"
            data-testid="workspace-board-project-filter"
          >
            <option value="">{t("workspaceBoard.allProjects")}</option>
            {projects.map((p) => (
              <option key={p.id} value={p.slug}>
                {p.name}
              </option>
            ))}
          </Select>
          {selected && (
            <Link
              href={`/p/${selected.slug}/board`}
              className="text-xs text-gray-400 transition-colors hover:text-gray-200"
              data-testid="workspace-board-open-project"
            >
              {t("workspaceBoard.openProjectBoard")}
            </Link>
          )}
          {/* Start from a ticket's key in a project's tracker (the dialog asks which project
              when every project is in view). */}
          <RequirePermission permission="TasksCreate">
            <ImportTicketButton
              projects={(selected ? [selected] : projects).map((p) => ({ slug: p.slug, name: p.name }))}
              onImported={(taskId) => updateSearchParams((p) => p.set("task", taskId))}
            />
          </RequirePermission>
          <RequirePermission permission="TasksCreate">
            {selected || projects.length === 1 ? (
              // One project in view: a new task goes there, as on its own board.
              <Link href={`/p/${(selected ?? projects[0]).slug}/tasks/new?from=board`}>
                <Button size="sm">
                  {PLUS_ICON}
                  {t("boardPage.newTask")}
                </Button>
              </Link>
            ) : (
              // Every project in view: the task has to go somewhere, so the button asks where.
              <Dropdown
                align="right"
                trigger={
                  <Button size="sm" data-testid="workspace-board-new-task">
                    {PLUS_ICON}
                    {t("boardPage.newTask")}
                  </Button>
                }
                items={[
                  { key: "", label: t("workspaceBoard.newTaskIn"), disabled: true },
                  ...projects.map((p) => ({ key: p.slug, label: p.name })),
                ]}
                onSelect={(slug) => {
                  if (slug) router.push(`/p/${slug}/tasks/new?from=board`);
                }}
              />
            )}
          </RequirePermission>
        </div>
      </div>

      {unknownProject && (
        <div
          className="-mt-2 mb-3 mx-4 flex flex-wrap items-center gap-2 rounded-lg border border-amber-500/20 bg-amber-500/5 px-3 py-2 text-xs text-amber-300 lg:mx-8"
          role="status"
          data-testid="workspace-board-unknown-project"
        >
          {t("workspaceBoard.unknownProject")}
          <button type="button" className="underline" onClick={() => setProject("")}>
            {t("workspaceBoard.showAll")}
          </button>
        </div>
      )}

      <TaskBoard
        // A different filter is a different board: remount so search and filters start fresh.
        key={selected?.id ?? "all"}
        boardData={board}
        projectId={selected?.slug ?? ""}
        projectUlid={selected?.id ?? ""}
        workspace={{ project: selected?.slug ?? null, projectUlids }}
      />
    </div>
  );
}
