import { useEffect, useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { Link } from "@/i18n/navigation";
import { OpenInEditorButton } from "@/components/task/OpenInEditorButton";
import { ChangedFileTree } from "./ChangedFileTree";
import { DiffEditor } from "./DiffEditor";
import type { WorkspaceModel, WorkspaceTotals } from "./workspace-model";
import type { LoadState } from "./use-live-workspace";
import type { Task } from "@/lib/types";

interface CodePaneProps {
  task: Task | null;
  projectId: string;
  model: WorkspaceModel;
  totals: WorkspaceTotals;
  loadState: LoadState;
  agentActive: boolean;
  selectedPath: string | null;
  onSelectPath: (path: string | null) => void;
  className?: string;
}

/**
 * The right-hand side of the Code View: toolbar (branch → base, counts, live state, actions),
 * the changed-file tree, and the diff of the selected file. Follows the agent — while nothing
 * is selected the file it last touched is shown — until the user picks a file themselves.
 */
function CodePane({
  task,
  projectId,
  model,
  totals,
  loadState,
  agentActive,
  selectedPath,
  onSelectPath,
  className,
}: CodePaneProps) {
  const t = useTranslations("dashboard.codeView");
  const files = useMemo(() => Object.values(model.files), [model.files]);
  const [treeCollapsed, setTreeCollapsed] = useState(false);

  // Code-only mode: the same pane, fixed over the whole viewport — nothing remounts, so the
  // live model, selection and scroll positions all survive entering and leaving. Keyed to the
  // task id: switching or deselecting the task drops the overlay by derivation, no effect.
  const [fsFor, setFsFor] = useState<string | null>(null);
  const fullscreen = fsFor !== null && fsFor === task?.id;
  useEffect(() => {
    if (!fullscreen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setFsFor(null);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [fullscreen]);

  // Follow-the-agent: no explicit selection → show the file it is working on. An explicit
  // selection sticks until that file disappears (rename/baseline replace).
  const shownPath = selectedPath && model.files[selectedPath] ? selectedPath : model.activePath;
  const shownFile = shownPath ? (model.files[shownPath] ?? null) : null;

  useEffect(() => {
    if (selectedPath && !model.files[selectedPath]) onSelectPath(null);
  }, [selectedPath, model.files, onSelectPath]);

  const branch = model.baseline.branchName ?? task?.branchName ?? null;
  const base = model.baseline.baseBranch;

  const sourceLabel =
    loadState === "loading"
      ? t("toolbar.loading")
      : loadState === "error"
        ? t("toolbar.baselineFailed")
        : model.baseline.source === "live"
          ? t("toolbar.fromLive")
          : model.baseline.source === "snapshot"
          ? t("toolbar.fromSnapshot")
          : model.baseline.source === "pr"
            ? t("toolbar.fromPr")
            : totals.hasLive
              ? t("toolbar.previewOnly")
              : null;

  if (!task) {
    return (
      <div className={cn("flex h-full items-center justify-center p-8 text-center", className)}>
        <div className="max-w-sm">
          <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.25" className="mx-auto mb-4 text-gray-400 opacity-60" aria-hidden="true">
            <path d="M8 9l-4 3 4 3M16 9l4 3-4 3M13 5l-2 14" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <h3 className="font-display text-base font-semibold text-white">{t("empty.title")}</h3>
          <p className="mt-2 text-sm text-gray-400">{t("empty.hint")}</p>
        </div>
      </div>
    );
  }

  return (
    <div
      className={cn(
        "flex min-h-0 flex-col",
        fullscreen ? "fixed inset-0 z-50 h-auto bg-surface-app" : "h-full",
        className
      )}
      role={fullscreen ? "dialog" : undefined}
      aria-modal={fullscreen ? true : undefined}
      aria-label={fullscreen ? t("tree.fullscreenLabel") : undefined}
    >
      {/* Toolbar */}
      <div className="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-1.5 border-b border-white/[0.06] px-3 py-2">
        <button
          type="button"
          onClick={() => setTreeCollapsed((c) => !c)}
          className="rounded-md p-1 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
          aria-label={treeCollapsed ? t("toolbar.showTree") : t("toolbar.hideTree")}
          aria-pressed={!treeCollapsed}
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
            <rect x="2" y="3" width="12" height="10" rx="1.5" />
            <path d="M6 3v10" />
          </svg>
        </button>

        {branch && (
          <span className="flex min-w-0 items-center gap-1.5 font-mono text-xs text-gray-300">
            <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" className="shrink-0 text-gray-400" aria-hidden="true">
              <circle cx="4" cy="3.5" r="1.5" /><circle cx="4" cy="12.5" r="1.5" /><circle cx="12" cy="6" r="1.5" />
              <path d="M4 5v6M12 7.5c0 2-2 3-4 3.5" strokeLinecap="round" />
            </svg>
            <span className="truncate">{branch}</span>
            {base && (
              <>
                <span className="text-gray-400">→</span>
                <span className="truncate text-gray-400">{base}</span>
              </>
            )}
          </span>
        )}

        <span className="text-xs text-gray-400">{t("toolbar.files", { count: totals.fileCount })}</span>
        <span className="font-mono text-xs tabular-nums">
          <span className="text-emerald-400">+{totals.additions}</span>{" "}
          <span className="text-red-400">-{totals.deletions}</span>
        </span>

        {agentActive ? (
          <span className="flex items-center gap-1.5 text-xs text-cyan-400">
            <span className="relative flex h-2 w-2" aria-hidden="true">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-cyan-400 opacity-75" />
              <span className="relative inline-flex h-2 w-2 rounded-full bg-cyan-400" />
            </span>
            {t("toolbar.live")}
          </span>
        ) : null}

        {sourceLabel && <span className="text-xs text-gray-400">{sourceLabel}</span>}
        {model.baseline.truncated && (
          <span className="text-xs text-amber-400">{t("toolbar.truncated")}</span>
        )}
        {model.baseline.unavailable && (
          <span className="text-xs text-amber-400">{t("toolbar.unavailable")}</span>
        )}

        <span className="ml-auto flex items-center gap-2">
          {task.prUrl && (
            <Link
              href={`/p/${projectId}/tasks/${task.number}/review`}
              className="rounded-md px-2 py-1 text-xs text-gray-300 transition-colors hover:bg-white/[0.06] hover:text-white"
            >
              {t("toolbar.reviewPr")}
            </Link>
          )}
          <OpenInEditorButton taskId={task.id} compact />
        </span>
      </div>

      {/* Tree + editor */}
      <div className="flex min-h-0 flex-1">
        {!treeCollapsed && (
          <div className="flex w-60 shrink-0 flex-col border-r border-white/[0.06]">
            <div className="flex shrink-0 items-center justify-between border-b border-white/[0.06] py-1 pl-3 pr-1.5">
              <span className="text-label">{t("tree.label")}</span>
              <button
                type="button"
                onClick={() => setFsFor(fullscreen ? null : task.id)}
                className="rounded-md p-1 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
                aria-label={fullscreen ? t("tree.exitFullscreen") : t("tree.enterFullscreen")}
                title={fullscreen ? t("tree.exitFullscreen") : t("tree.enterFullscreen")}
                aria-pressed={fullscreen}
              >
                {fullscreen ? (
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
                    <path d="M6 2v4H2M10 2v4h4M6 14v-4H2M10 14v-4h4" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                ) : (
                  <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden="true">
                    <path d="M6 2H2v4M10 2h4v4M6 14H2v-4M10 14h4v-4" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                )}
              </button>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto">
              <ChangedFileTree
                files={files}
                selectedPath={shownPath}
                activePath={model.activePath}
                onSelect={onSelectPath}
              />
            </div>
          </div>
        )}
        <div className="min-w-0 flex-1">
          <DiffEditor file={shownFile} agentActive={agentActive} />
        </div>
      </div>
    </div>
  );
}

export { CodePane };
