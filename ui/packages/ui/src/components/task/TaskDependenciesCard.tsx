import { useEffect, useMemo, useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { GlassCard } from "@/components/ui";
import { usePermissions } from "@/hooks/use-permissions";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { addTaskDependency, removeTaskDependency } from "@/lib/actions";
import { TASK_STATUS_MAP } from "@/lib/constants";
import type { Task, TaskDependencyItem } from "@/lib/types";

interface TaskDependenciesCardProps {
  task: Task;
  projectId: string;
  /** Called after add/remove so the parent can refetch the task */
  onChanged?: () => void;
}

function DependencyRow({
  item,
  projectId,
  showSource,
  awaitingMergeLabel,
  onRemove,
  removeLabel,
  disabled,
}: {
  item: TaskDependencyItem;
  projectId: string;
  showSource: boolean;
  awaitingMergeLabel: string;
  onRemove?: (item: TaskDependencyItem) => void;
  removeLabel?: string;
  disabled?: boolean;
}) {
  const t = useTranslations("dashboard.taskMisc");
  // Satisfied = done AND merged; a Completed-but-unmerged blocker still blocks
  // (dependents build on merged code), so it keeps the "active" treatment plus
  // an explicit awaiting-merge label instead of the checkmark.
  //
  // Unless both ends are in the same cross-repo group: siblings code against the group's
  // shared contract rather than each other's merged source, so Completed alone clears the
  // edge. Showing those as awaiting-merge would report a group that is running normally as
  // stuck for as long as its gate holds the PRs — which is the whole life of the group.
  const isSatisfied =
    item.status === "Completed" && (item.isMerged || item.sameGroup);
  const isAwaitingMerge =
    item.status === "Completed" && !item.isMerged && !item.sameGroup;
  const statusColor = TASK_STATUS_MAP[item.status]?.color ?? "#6b7280";

  return (
    <div className="group rounded-lg px-2 py-1.5 transition-colors hover:bg-white/[0.03]">
      <div className="flex items-center gap-2">
        {isSatisfied ? (
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="#34d399" strokeWidth="2.5" className="shrink-0">
            <path d="M5 13l4 4L19 7" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : (
          <span
            className="h-2 w-2 shrink-0 rounded-full"
            style={{ backgroundColor: isAwaitingMerge ? "#f59e0b" : statusColor }}
          />
        )}
        <Link
          href={`/p/${projectId}/tasks/${item.number}`}
          className="min-w-0 flex-1"
          title={item.title}
        >
          <span className="font-mono text-xs text-gray-400">{item.code}</span>
          <span
            className={cn(
              "ml-1.5 text-xs transition-colors group-hover:text-white",
              isSatisfied ? "text-gray-400 line-through" : "text-gray-300"
            )}
          >
            {item.title}
          </span>
          {isAwaitingMerge && (
            <span className="ml-1.5 whitespace-nowrap rounded border border-amber-500/25 bg-amber-500/10 px-1 text-xs font-medium uppercase text-amber-400">
              {awaitingMergeLabel}
            </span>
          )}
        </Link>
        {showSource && item.source === "Ai" && (
          <span className="shrink-0 rounded border border-violet-500/25 bg-violet-500/10 px-1 text-xs font-semibold uppercase text-violet-400">
            {t("aiBadge")}
          </span>
        )}
        {onRemove && (
          <button
            onClick={() => onRemove(item)}
            disabled={disabled}
            aria-label={removeLabel}
            className="shrink-0 rounded p-1 text-gray-400 opacity-0 transition-all hover:bg-red-500/10 hover:text-red-400 group-hover:opacity-100"
          >
            <svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M12 4L4 12M4 4l8 8" strokeLinecap="round" />
            </svg>
          </button>
        )}
      </div>
      {item.reason && (
        <p className="mt-0.5 pl-4 text-xs italic leading-relaxed text-gray-400">
          {item.reason}
        </p>
      )}
    </div>
  );
}

/**
 * Sidebar card for the task's dependency graph. "Waiting on" lists the tasks that must
 * complete before agents pick this one up (removable — including wrong AI-detected ones);
 * "Blocks" lists tasks waiting on this one (read-only; remove from the other side).
 */
function TaskDependenciesCard({ task, projectId, onChanged }: TaskDependenciesCardProps) {
  const t = useTranslations("dashboard.taskDependencies");
  const awaitingMergeLabel = t("awaitingMerge");
  const { has } = usePermissions();
  const canEdit = has("TasksEdit");
  const withToken = useClientApi();
  const [isPending, startTransition] = useTransition();

  const [dependsOn, setDependsOn] = useState<TaskDependencyItem[]>(task.dependsOn ?? []);
  const [dependents, setDependents] = useState<TaskDependencyItem[]>(task.dependents ?? []);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const [candidates, setCandidates] = useState<Task[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Re-sync when the parent re-fetches the task.
  //
  // Adjusted during render rather than in an effect, which is React's own answer to "reset state
  // when a prop changes": React re-runs this component immediately, before touching the DOM, so
  // there is no second commit and no frame in which the card shows the previous task's edges.
  // The effect version rendered stale rows first and then corrected them — the cascading render
  // the lint rule is named after.
  //
  // Reference comparison, not deep: every refetch produces fresh arrays, which is exactly the
  // condition the old dependency array keyed on, so the sync fires in the same cases as before.
  const [syncedFrom, setSyncedFrom] = useState<{
    dependsOn?: TaskDependencyItem[];
    dependents?: TaskDependencyItem[];
  }>({ dependsOn: task.dependsOn, dependents: task.dependents });

  if (
    syncedFrom.dependsOn !== task.dependsOn ||
    syncedFrom.dependents !== task.dependents
  ) {
    setSyncedFrom({ dependsOn: task.dependsOn, dependents: task.dependents });
    setDependsOn(task.dependsOn ?? []);
    setDependents(task.dependents ?? []);
  }

  // Candidates load lazily when the picker opens. Done tasks are archived, but a
  // Done-but-unmerged one is still a valid blocker — pull those in from the archive.
  useEffect(() => {
    if (!pickerOpen || candidates !== null) return;
    let cancelled = false;
    Promise.all([
      withToken((token) => api.tasks.list(projectId, token)),
      withToken((token) => api.tasks.listFiltered(projectId, { isArchived: true }, token)),
    ])
      .then(([active, archived]) => {
        if (!cancelled)
          setCandidates([
            ...active,
            ...archived.filter((c) => c.status === "Completed" && !c.isMerged),
          ]);
      })
      .catch(() => {
        if (!cancelled) setCandidates([]);
      });
    return () => {
      cancelled = true;
    };
  }, [pickerOpen, candidates, projectId, withToken]);

  const visibleCandidates = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    const existing = new Set(dependsOn.map((d) => d.taskId));
    return (candidates ?? []).filter(
      (c) =>
        c.id !== task.id &&
        // Done-but-unmerged tasks remain valid blockers (dependents wait for the
        // merge); only tasks with nothing left to wait on are excluded.
        !(c.status === "Completed" && c.isMerged) &&
        c.status !== "Archived" &&
        !existing.has(c.id) &&
        (!needle ||
          c.title.toLowerCase().includes(needle) ||
          c.code.toLowerCase().includes(needle))
    );
  }, [candidates, filter, dependsOn, task.id]);

  const handleAdd = (candidateId: string) => {
    setError(null);
    startTransition(async () => {
      try {
        const result = await addTaskDependency(task.id, candidateId);
        setDependsOn(result.dependsOn);
        setDependents(result.dependents);
        onChanged?.();
      } catch {
        // Most common rejection: the edge would create a cycle
        setError(t("cycleError"));
      }
    });
  };

  const handleRemove = (item: TaskDependencyItem) => {
    setError(null);
    const previous = dependsOn;
    setDependsOn(previous.filter((d) => d.taskId !== item.taskId));
    startTransition(async () => {
      try {
        await removeTaskDependency(task.id, item.taskId);
        onChanged?.();
      } catch {
        setDependsOn(previous);
      }
    });
  };

  const isEmpty = dependsOn.length === 0 && dependents.length === 0;

  // Unmet = not (done AND merged), or for a same-group sibling, not done. While any remain,
  // agents skip the task and forward lane moves are rejected server-side — tell the user why,
  // up front. Mirrors TaskDependencyQueries.IsUnmetItem on the server; the two must agree.
  const hasUnmet =
    task.currentLane !== "Done" &&
    dependsOn.some(
      (d) => d.status !== "Completed" || (!d.isMerged && !d.sameGroup)
    );

  return (
    <GlassCard padding="md">
      <div className="mb-3 flex items-center justify-between">
        <h4 className="text-label">
          {t("title")}
        </h4>
        {canEdit && (
          <button
            onClick={() => {
              setPickerOpen((v) => !v);
              setError(null);
            }}
            disabled={isPending}
            className={cn(
              "flex items-center gap-1 rounded-lg px-2 py-1 text-xs transition-colors",
              pickerOpen
                ? "bg-white/[0.08] text-white"
                : "text-gray-400 hover:bg-white/[0.04] hover:text-gray-200"
            )}
          >
            {pickerOpen ? (
              t("done")
            ) : (
              <>
                <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M12 5v14m-7-7h14" strokeLinecap="round" />
                </svg>
                {t("add")}
              </>
            )}
          </button>
        )}
      </div>

      {isEmpty && !pickerOpen && <p className="text-xs text-gray-400">{t("empty")}</p>}

      {hasUnmet && (
        <div className="mb-3 flex items-start gap-2 rounded-lg border border-amber-500/25 bg-amber-500/10 px-2.5 py-2">
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="#f59e0b"
            strokeWidth="2"
            className="mt-0.5 shrink-0"
          >
            <path d="M12 9v4m0 4h.01M10.3 3.9L1.8 18a2 2 0 001.7 3h17a2 2 0 001.7-3L13.7 3.9a2 2 0 00-3.4 0z" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <p className="text-xs leading-relaxed text-amber-200/90">{t("blockedBanner")}</p>
        </div>
      )}

      {dependsOn.length > 0 && (
        <div className="mb-2">
          <p className="mb-1 px-1 text-label text-amber-400/70">
            {t("waitingOn")}
          </p>
          <div className="space-y-0.5">
            {dependsOn.map((item) => (
              <DependencyRow
                key={item.taskId}
                item={item}
                projectId={projectId}
                showSource
                awaitingMergeLabel={awaitingMergeLabel}
                onRemove={canEdit ? handleRemove : undefined}
                removeLabel={t("remove")}
                disabled={isPending}
              />
            ))}
          </div>
        </div>
      )}

      {dependents.length > 0 && (
        <div>
          <p className="mb-1 px-1 text-label">
            {t("blocks")}
          </p>
          <div className="space-y-0.5">
            {dependents.map((item) => (
              <DependencyRow
                key={item.taskId}
                item={item}
                projectId={projectId}
                showSource={false}
                awaitingMergeLabel={awaitingMergeLabel}
              />
            ))}
          </div>
        </div>
      )}

      {error && <p className="mt-2 text-xs text-red-400">{error}</p>}

      {/* Inline picker */}
      {pickerOpen && (
        <div className="mt-3 border-t border-white/[0.06] pt-3">
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder={t("filterPlaceholder")}
            className={cn(
              "mb-2 w-full rounded-lg border border-white/[0.08] bg-white/[0.03] px-2.5 py-1.5",
              "text-xs text-gray-200 placeholder:text-gray-500",
              "focus:border-primary-500/50 focus:outline-none"
            )}
          />
          <div className="max-h-56 space-y-0.5 overflow-y-auto">
            {candidates === null ? (
              <p className="px-2 py-3 text-center text-xs text-gray-400">{t("loading")}</p>
            ) : visibleCandidates.length === 0 ? (
              <p className="px-2 py-3 text-center text-xs text-gray-400">{t("noMatches")}</p>
            ) : (
              visibleCandidates.map((c) => (
                <button
                  key={c.id}
                  onClick={() => handleAdd(c.id)}
                  disabled={isPending}
                  className="flex w-full items-center gap-2 rounded-lg px-1.5 py-1.5 text-left transition-colors hover:bg-white/[0.04]"
                >
                  <span className="text-xs text-gray-400">{c.code}</span>
                  <span className="truncate text-xs text-gray-300">{c.title}</span>
                </button>
              ))
            )}
          </div>
        </div>
      )}

      <p className="mt-3 text-xs leading-relaxed text-gray-400">{t("hint")}</p>
    </GlassCard>
  );
}

export { TaskDependenciesCard };
