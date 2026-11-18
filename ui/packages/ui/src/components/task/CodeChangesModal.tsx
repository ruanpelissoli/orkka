import { useState, useEffect, useMemo, useRef, useCallback } from "react";
import { useTranslations } from "use-intl";
import { Modal, Select, Skeleton } from "@/components/ui";
import { DiffFileCard } from "@/components/pr/DiffFileCard";
import { parseCommitDiff } from "@/components/repos/commit-diff-parser";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { cn, formatRelativeTime } from "@/lib/utils";
import type { CodeChangeSnapshotDiff, CodeChangeSnapshotSummary } from "@/lib/types";

interface CodeChangesModalProps {
  taskId: string;
  /** Stored snapshots, newest first (as the API returns them). */
  snapshots: CodeChangeSnapshotSummary[];
  open: boolean;
  onClose: () => void;
}

const FILE_STATUS_TONE: Record<string, string> = {
  added: "text-emerald-400",
  modified: "text-amber-400",
  removed: "text-red-400",
  renamed: "text-primary-400",
  copied: "text-violet-400",
};

const FILE_STATUS_GLYPH: Record<string, string> = {
  added: "A",
  modified: "M",
  removed: "D",
  renamed: "R",
  copied: "C",
};

/**
 * The permanent record of what the agents changed, straight from the DB — unlike the PR view
 * this works after the worktree is GC'd and the branch is merged away.
 *
 * Reviewing a multi-file diff in a cramped scroll was the whole complaint here, so this is a
 * full-viewport, two-pane layout: changed files on the left for one-click jumps (with a
 * scroll-spy marking where you are), the diff on the right. On phones the sidebar folds into
 * a jump select — a 288px rail on a 375px screen would leave no room for the diff itself.
 */
function CodeChangesModal({ taskId, snapshots, open, onClose }: CodeChangesModalProps) {
  const t = useTranslations("dashboard.codeChanges");
  const withToken = useClientApi();

  const [selectedId, setSelectedId] = useState<string | null>(
    snapshots.length > 0 ? snapshots[0].id : null
  );
  const [diffCache, setDiffCache] = useState<Record<string, CodeChangeSnapshotDiff>>({});
  // The snapshot whose diff last failed to load; cleared to retry it.
  const [failedId, setFailedId] = useState<string | null>(null);
  const [activeFile, setActiveFile] = useState<string | null>(null);

  const scrollPaneRef = useRef<HTMLDivElement>(null);
  const fileRefs = useRef(new Map<string, HTMLDivElement>());

  const selected = snapshots.find((s) => s.id === selectedId) ?? null;
  const loaded = selectedId ? (diffCache[selectedId] ?? null) : null;
  const diff = loaded?.diff ?? null;
  const error = selectedId !== null && loaded === null && failedId === selectedId;
  const loading = open && selectedId !== null && loaded === null && !error;

  // Reopening the modal retries a diff that failed to load.
  const [prevOpen, setPrevOpen] = useState(open);
  if (open !== prevOpen) {
    setPrevOpen(open);
    if (open) setFailedId(null);
  }

  useEffect(() => {
    if (!open || !selectedId || diffCache[selectedId] !== undefined || failedId === selectedId) return;
    let cancelled = false;

    withToken((token) => api.tasks.codeChangeDiff(taskId, selectedId, token))
      .then((data) => {
        if (!cancelled) setDiffCache((cache) => ({ ...cache, [data.id]: data }));
      })
      .catch(() => {
        if (!cancelled) setFailedId(selectedId);
      });

    return () => {
      cancelled = true;
    };
  }, [open, selectedId, taskId, withToken, diffCache, failedId]);

  const files = useMemo(() => (diff ? parseCommitDiff(diff) : []), [diff]);

  // Scroll-spy: the sidebar marks the file whose card currently tops the diff pane, so a
  // long scroll never loses "where am I". rootMargin biases to the reading line rather
  // than the exact pane top.
  useEffect(() => {
    const pane = scrollPaneRef.current;
    if (!pane || files.length === 0) return;

    const observer = new IntersectionObserver(
      (entries) => {
        const topmost = entries
          .filter((e) => e.isIntersecting)
          .sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
        const filename = topmost?.target.getAttribute("data-filename");
        if (filename) setActiveFile(filename);
      },
      { root: pane, rootMargin: "0px 0px -70% 0px" }
    );

    for (const el of fileRefs.current.values()) observer.observe(el);
    return () => observer.disconnect();
  }, [files]);

  const jumpToFile = useCallback((filename: string) => {
    setActiveFile(filename);
    fileRefs.current.get(filename)?.scrollIntoView({ block: "start", behavior: "smooth" });
  }, []);

  const selectSnapshot = useCallback((id: string) => {
    setSelectedId(id);
    setFailedId(null);
    setActiveFile(null);
    scrollPaneRef.current?.scrollTo({ top: 0 });
  }, []);

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={t("title")}
      className="md:max-w-[min(96rem,calc(100vw-2rem))] md:h-[calc(100dvh-2rem)] md:max-h-[calc(100dvh-2rem)] h-[100dvh] max-h-[100dvh]"
      bodyClassName="flex flex-col overflow-hidden p-0"
    >
      {/* Snapshot picker + stats bar */}
      <div className="flex flex-wrap items-center gap-3 border-b border-white/[0.06] px-4 py-2.5">
        {snapshots.length > 1 && (
          <div className="flex flex-wrap items-center gap-1.5">
            {snapshots.map((snapshot) => (
              <button
                key={snapshot.id}
                type="button"
                onClick={() => selectSnapshot(snapshot.id)}
                className={cn(
                  "rounded-full px-2.5 py-1 text-xs font-medium transition-colors",
                  snapshot.id === selectedId
                    ? "bg-cyan-400/15 text-cyan-300"
                    : "bg-white/[0.04] text-gray-400 hover:bg-white/[0.08] hover:text-gray-200"
                )}
              >
                {t("session", {
                  lane: snapshot.lane,
                  when: formatRelativeTime(snapshot.capturedAt),
                })}
              </button>
            ))}
          </div>
        )}

        {selected && (
          <div className="flex min-w-0 flex-wrap items-center gap-3 text-xs">
            <span className="text-gray-400">
              {t("filesChanged", { count: selected.fileCount })}
            </span>
            <span className="font-mono text-emerald-400">+{selected.additions}</span>
            <span className="font-mono text-red-400">-{selected.deletions}</span>
            <span className="truncate font-mono text-gray-400">
              {selected.branchName} → {selected.baseBranch}
            </span>
          </div>
        )}
      </div>

      {loaded?.unavailable ? (
        <p className="border-b border-amber-500/10 bg-amber-500/[0.08] px-4 py-2 text-xs text-amber-400">
          {t("unavailable")}
        </p>
      ) : loaded?.currentBranch ? (
        <p className="border-b border-white/[0.06] px-4 py-2 text-xs text-gray-400">
          {t("currentBranch")}
        </p>
      ) : null}

      {loaded?.isTruncated && (
        <p className="border-b border-amber-500/10 bg-amber-500/[0.08] px-4 py-2 text-xs text-amber-400">
          {t("truncated")}
        </p>
      )}

      {/* Mobile file jumper — the sidebar has no room on a phone */}
      {files.length > 1 && (
        <div className="border-b border-white/[0.06] px-4 py-2 md:hidden">
          <Select
            size="sm"
            aria-label={t("filesChanged", { count: files.length })}
            value={activeFile ?? ""}
            onChange={(e) => jumpToFile(e.target.value)}
          >
            <option value="" disabled>
              {t("filesChanged", { count: files.length })}
            </option>
            {files.map((file) => (
              <option key={file.filename} value={file.filename}>
                {file.filename}
              </option>
            ))}
          </Select>
        </div>
      )}

      <div className="flex min-h-0 flex-1">
        {/* File rail */}
        {files.length > 0 && (
          <nav className="hidden w-72 shrink-0 overflow-y-auto border-r border-white/[0.06] py-2 md:block">
            {files.map((file) => {
              const isActive = file.filename === activeFile;
              return (
                <button
                  key={file.filename}
                  type="button"
                  onClick={() => jumpToFile(file.filename)}
                  title={file.filename}
                  className={cn(
                    "flex w-full items-center gap-2 border-l-2 px-3 py-1.5 text-left text-xs transition-colors",
                    isActive
                      ? "border-cyan-400/70 bg-cyan-400/[0.06] text-gray-100"
                      : "border-transparent text-gray-400 hover:bg-white/[0.04] hover:text-gray-200"
                  )}
                >
                  <span
                    className={cn(
                      "w-3 shrink-0 text-center font-mono text-xs font-bold",
                      FILE_STATUS_TONE[file.status] ?? FILE_STATUS_TONE.modified
                    )}
                  >
                    {FILE_STATUS_GLYPH[file.status] ?? "M"}
                  </span>
                  {/* rtl + ltr marks: long paths truncate on the LEFT so the filename —
                      the part that identifies the entry — stays visible. */}
                  <span className="min-w-0 flex-1 truncate font-mono" dir="rtl">
                    <bdi>{file.filename}</bdi>
                  </span>
                  <span className="shrink-0 font-mono text-xs">
                    <span className="text-emerald-400">+{file.additions}</span>{" "}
                    <span className="text-red-400">-{file.deletions}</span>
                  </span>
                </button>
              );
            })}
          </nav>
        )}

        {/* Diff pane */}
        <div ref={scrollPaneRef} className="min-w-0 flex-1 overflow-y-auto p-3 md:p-4">
          {loading ? (
            <div className="space-y-3">
              <Skeleton className="h-8 w-1/2" />
              <Skeleton className="h-24" />
              <Skeleton className="h-24" />
            </div>
          ) : error ? (
            <p className="py-6 text-center text-sm text-red-400">{t("error")}</p>
          ) : files.length === 0 ? (
            <p className="py-6 text-center text-sm text-gray-400">{t("empty")}</p>
          ) : (
            <div className="space-y-3">
              {files.map((file) => (
                <DiffFileCard
                  key={file.filename}
                  ref={(el) => {
                    if (el) fileRefs.current.set(file.filename, el);
                    else fileRefs.current.delete(file.filename);
                  }}
                  file={file}
                  readOnly
                />
              ))}
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
}

export { CodeChangesModal };
export type { CodeChangesModalProps };
