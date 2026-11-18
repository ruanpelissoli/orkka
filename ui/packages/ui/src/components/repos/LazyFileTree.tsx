import { useState, useEffect, useCallback } from "react";
import { useTranslations } from "use-intl";
import { Skeleton } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { cn } from "@/lib/utils";
import type { RepoTreeEntry } from "@/lib/types";

const ROOT = "";

/** The set without `path`, or the same set when it was not there. */
function withoutPath(set: Set<string>, path: string): Set<string> {
  if (!set.has(path)) return set;
  const next = new Set(set);
  next.delete(path);
  return next;
}

interface LazyFileTreeProps {
  projectId: string;
  repoId: string;
  defaultBranch: string;
  onFileSelect: (path: string) => void;
  selectedPath?: string | null;
  /** Paths already linked elsewhere (e.g. a task's context files) — marked with a check. */
  linkedPaths?: Set<string>;
}

/**
 * Directory-at-a-time file tree over the repo `tree` endpoint. The root call
 * also syncs the repo (git fetch) so it can take a few seconds; child levels
 * are fetched on first expand and cached in state. Visually mirrors the eager
 * FileTree component so the two are interchangeable.
 */
function LazyFileTree({
  projectId,
  repoId,
  defaultBranch,
  onFileSelect,
  selectedPath = null,
  linkedPaths,
}: LazyFileTreeProps) {
  const t = useTranslations("dashboard.repoMisc.fileTree");
  const withToken = useClientApi();

  // Loaded children per directory path (ROOT = repo root).
  const [childrenByPath, setChildrenByPath] = useState<Map<string, RepoTreeEntry[]>>(
    () => new Map()
  );
  const [loadingPaths, setLoadingPaths] = useState<Set<string>>(() => new Set());
  const [errorPaths, setErrorPaths] = useState<Set<string>>(() => new Set());
  const [expandedPaths, setExpandedPaths] = useState<Set<string>>(() => new Set());

  const fetchLevel = useCallback(
    (path: string) =>
      withToken((token) =>
        api.repoHistory.tree(
          projectId,
          repoId,
          { path: path || undefined, branch: defaultBranch },
          token
        )
      ),
    [projectId, repoId, defaultBranch, withToken]
  );

  const loadLevel = useCallback(
    async (path: string) => {
      setLoadingPaths((prev) => new Set(prev).add(path));
      setErrorPaths((prev) => withoutPath(prev, path));

      try {
        const entries = await fetchLevel(path);
        setChildrenByPath((prev) => new Map(prev).set(path, entries));
      } catch {
        setErrorPaths((prev) => new Set(prev).add(path));
      } finally {
        setLoadingPaths((prev) => withoutPath(prev, path));
      }
    },
    [fetchLevel]
  );

  // Root level on mount. Its loading state is implied (no children and no
  // error yet), so the effect only writes the result once it arrives.
  useEffect(() => {
    let cancelled = false;
    fetchLevel(ROOT)
      .then((entries) => {
        if (cancelled) return;
        setChildrenByPath((prev) => new Map(prev).set(ROOT, entries));
        setErrorPaths((prev) => withoutPath(prev, ROOT));
      })
      .catch(() => {
        if (!cancelled) setErrorPaths((prev) => new Set(prev).add(ROOT));
      });
    return () => {
      cancelled = true;
    };
  }, [fetchLevel]);

  const handleToggleDir = useCallback(
    (path: string) => {
      setExpandedPaths((prev) => {
        const next = new Set(prev);
        if (next.has(path)) {
          next.delete(path);
        } else {
          next.add(path);
        }
        return next;
      });
      // Fetch on first expand only — cached levels are reused.
      if (
        !expandedPaths.has(path) &&
        !childrenByPath.has(path) &&
        !loadingPaths.has(path)
      ) {
        loadLevel(path);
      }
    },
    [expandedPaths, childrenByPath, loadingPaths, loadLevel]
  );

  const renderLevel = (path: string, depth: number): React.ReactNode => {
    if (errorPaths.has(path)) {
      return <ErrorRow depth={depth} onRetry={() => loadLevel(path)} />;
    }

    const entries = childrenByPath.get(path);
    if (!entries) return null;

    if (entries.length === 0) {
      return (
        <div
          className="px-2 py-1 text-xs text-gray-400"
          style={{ paddingLeft: `${depth * 16 + 28}px` }}
        >
          {t("emptyDir")}
        </div>
      );
    }

    return entries.map((entry) =>
      entry.isDirectory ? (
        <DirectoryRow
          key={entry.path}
          entry={entry}
          depth={depth}
          expanded={expandedPaths.has(entry.path)}
          loading={loadingPaths.has(entry.path)}
          onToggle={() => handleToggleDir(entry.path)}
        >
          {expandedPaths.has(entry.path) && renderLevel(entry.path, depth + 1)}
        </DirectoryRow>
      ) : (
        <FileRow
          key={entry.path}
          entry={entry}
          depth={depth}
          selected={selectedPath === entry.path}
          linked={linkedPaths?.has(entry.path) ?? false}
          onSelect={() => onFileSelect(entry.path)}
        />
      )
    );
  };

  // Root loading (can take seconds — the call syncs the repo).
  if (!childrenByPath.has(ROOT) && !errorPaths.has(ROOT)) {
    const widths = ["w-full", "w-3/4", "w-5/6", "w-3/5"];
    return (
      <div className="space-y-2 p-3">
        {Array.from({ length: 8 }).map((_, i) => (
          <Skeleton key={i} className={cn("h-6", widths[i % widths.length])} />
        ))}
      </div>
    );
  }

  if (errorPaths.has(ROOT)) {
    return (
      <div className="px-3 py-6 text-center">
        <p className="mb-2 text-sm text-red-400">{t("loadFailed")}</p>
        <button
          onClick={() => loadLevel(ROOT)}
          className="text-sm text-gray-400 underline transition-colors hover:text-gray-200"
        >
          {t("retry")}
        </button>
      </div>
    );
  }

  if ((childrenByPath.get(ROOT)?.length ?? 0) === 0) {
    return (
      <div className="px-3 py-6 text-center text-sm text-gray-400">
        {t("noFiles")}
      </div>
    );
  }

  return <div className="py-1">{renderLevel(ROOT, 0)}</div>;
}

function DirectoryRow({
  entry,
  depth,
  expanded,
  loading,
  onToggle,
  children,
}: {
  entry: RepoTreeEntry;
  depth: number;
  expanded: boolean;
  loading: boolean;
  onToggle: () => void;
  children?: React.ReactNode;
}) {
  return (
    <>
      <button
        onClick={onToggle}
        className="flex w-full items-center gap-1.5 px-2 py-1 text-sm text-gray-300 transition-colors hover:bg-white/[0.04]"
        style={{ paddingLeft: `${depth * 16 + 8}px` }}
      >
        <svg
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          className={cn("shrink-0 transition-transform", expanded && "rotate-90")}
        >
          <path d="M9 6l6 6-6 6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>

        <svg
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="currentColor"
          className="shrink-0 text-primary-400/80"
        >
          {expanded ? (
            <path d="M2 7.5A2.5 2.5 0 014.5 5h4.172a2 2 0 011.414.586l.828.828A2 2 0 0012.328 7H19.5A2.5 2.5 0 0122 9.5v0a.5.5 0 01-.491.592l-1.166.07A5 5 0 0015.5 13H4a2 2 0 01-2-2V7.5z" />
          ) : (
            <path d="M2 7.5A2.5 2.5 0 014.5 5h4.172a2 2 0 011.414.586l.828.828A2 2 0 0012.328 7H19.5A2.5 2.5 0 0122 9.5V17a2.5 2.5 0 01-2.5 2.5h-15A2.5 2.5 0 012 17V7.5z" />
          )}
        </svg>

        <span className="truncate">{entry.name}</span>

        {loading && (
          <span className="ml-1 h-3 w-3 shrink-0 animate-spin rounded-full border border-gray-600 border-t-gray-300" />
        )}
      </button>

      {children}
    </>
  );
}

function FileRow({
  entry,
  depth,
  selected,
  linked = false,
  onSelect,
}: {
  entry: RepoTreeEntry;
  depth: number;
  selected: boolean;
  linked?: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      onClick={onSelect}
      className={cn(
        "flex w-full items-center gap-1.5 px-2 py-1 text-sm transition-colors hover:bg-white/[0.04]",
        selected ? "bg-white/[0.08] text-white" : "text-gray-400"
      )}
      style={{ paddingLeft: `${depth * 16 + 8}px` }}
    >
      <div className="w-3.5 shrink-0" />
      <svg
        width="14"
        height="14"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        className="shrink-0 text-gray-400"
      >
        <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
        <polyline points="14 2 14 8 20 8" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      <span className="truncate">{entry.name}</span>
      {linked && (
        <svg
          width="12"
          height="12"
          viewBox="0 0 16 16"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          className="ml-auto shrink-0 text-primary-300"
        >
          <path d="M3 8.5l3.5 3.5L13 4.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      )}
    </button>
  );
}

function ErrorRow({ depth, onRetry }: { depth: number; onRetry: () => void }) {
  const t = useTranslations("dashboard.repoMisc.fileTree");
  return (
    <div
      className="flex items-center gap-2 px-2 py-1 text-xs text-red-400"
      style={{ paddingLeft: `${depth * 16 + 28}px` }}
    >
      <span>{t("rowLoadFailed")}</span>
      <button
        onClick={onRetry}
        className="text-gray-400 underline transition-colors hover:text-gray-200"
      >
        {t("retry")}
      </button>
    </div>
  );
}

export { LazyFileTree };
