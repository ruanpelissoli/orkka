import { useState, useRef, useEffect, useCallback } from "react";
import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { RepoProviderIcon } from "@/components/ui/brand-icons";
import { FileContentViewer } from "@/components/repos/FileContentViewer";
import { LazyFileTree } from "@/components/repos/LazyFileTree";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { cn } from "@/lib/utils";
import type { ProjectRepo } from "@/lib/types";

interface RepoFilePickerPanelProps {
  projectId: string;
  repo: ProjectRepo | null;
  linkedFiles: string[];
  onToggleLink: (path: string) => void;
  className?: string;
}

/**
 * Repo browser for the task-creation form: filename search + lazy file tree,
 * with a code viewer that takes over the whole panel when a file is opened.
 * Linking hands the path back to the form's contextFiles — the panel owns no
 * persistence. Lives inside a <form>, so every control must stay type="button"
 * and the search input must swallow Enter.
 */
function RepoFilePickerPanel({
  projectId,
  repo,
  linkedFiles,
  onToggleLink,
  className,
}: RepoFilePickerPanelProps) {
  const t = useTranslations("dashboard");
  const withToken = useClientApi();

  const [view, setView] = useState<"browse" | "viewer">("browse");
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [content, setContent] = useState<string | null>(null);
  const [loadingContent, setLoadingContent] = useState(false);
  const [contentError, setContentError] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);

  const [query, setQuery] = useState("");
  // The last search's answer, tagged with what it searched for; "searching"
  // is derived as "no answer for the current query yet".
  const [searched, setSearched] = useState<{ key: string; files: string[] } | null>(null);

  const searchDebounceRef = useRef<ReturnType<typeof setTimeout>>(null);
  const searchRequestRef = useRef(0);
  const contentRequestRef = useRef(0);

  const searchKey = repo && query !== "" ? `${projectId}/${repo.id}/${query}` : null;
  const isSearching = searchKey !== null && searched?.key !== searchKey;
  const results = searchKey !== null && searched?.key === searchKey ? searched.files : [];

  // Debounced filename search (same shape as MentionTextarea's mention search).
  useEffect(() => {
    if (!repo || query === "") return;

    const key = `${projectId}/${repo.id}/${query}`;
    const currentId = ++searchRequestRef.current;

    if (searchDebounceRef.current) clearTimeout(searchDebounceRef.current);
    searchDebounceRef.current = setTimeout(async () => {
      try {
        const files = await withToken((token) =>
          api.repos.searchFiles(projectId, repo.id, query, token)
        );
        if (searchRequestRef.current === currentId) setSearched({ key, files });
      } catch {
        if (searchRequestRef.current === currentId) setSearched({ key, files: [] });
      }
    }, 250);

    return () => {
      if (searchDebounceRef.current) clearTimeout(searchDebounceRef.current);
    };
  }, [query, projectId, repo, withToken]);

  const openFile = useCallback(
    async (path: string) => {
      if (!repo) return;
      setSelectedPath(path);
      setView("viewer");
      setLoadingContent(true);
      setContentError(false);

      const currentId = ++contentRequestRef.current;
      try {
        const text = await withToken((token) =>
          api.repos.getFileContent(projectId, repo.id, path, token)
        );
        if (contentRequestRef.current === currentId) setContent(text);
      } catch {
        if (contentRequestRef.current === currentId) {
          setContent(null);
          setContentError(true);
        }
      } finally {
        if (contentRequestRef.current === currentId) setLoadingContent(false);
      }
    },
    [projectId, repo, withToken]
  );

  // Escape exits fullscreen without touching the rest of the panel state.
  useEffect(() => {
    if (!fullscreen) return;
    function onKeyDown(e: globalThis.KeyboardEvent) {
      if (e.key === "Escape") setFullscreen(false);
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [fullscreen]);

  if (!repo) {
    return (
      <GlassCard
        padding="lg"
        className={cn("flex h-[70vh] xl:h-[calc(100vh-7rem)] items-center justify-center", className)}
      >
        <div className="text-center text-gray-400">
          <RepoProviderIcon provider="other" size={32} className="mx-auto mb-3 opacity-40" />
          <p className="text-sm">{t("repoFilePanel.noRepo")}</p>
        </div>
      </GlassCard>
    );
  }

  const isLinked = selectedPath !== null && linkedFiles.includes(selectedPath);

  const viewerActions = (
    <div className="flex shrink-0 items-center gap-1.5 pl-3">
      <button
        type="button"
        onClick={() => selectedPath && onToggleLink(selectedPath)}
        disabled={!selectedPath}
        className={cn(
          "flex items-center gap-1.5 rounded-lg px-2.5 py-1 text-xs font-medium transition-colors",
          isLinked
            ? "bg-primary-500/15 text-primary-300 hover:bg-primary-500/25"
            : "bg-white/[0.06] text-gray-300 hover:bg-white/[0.1]"
        )}
      >
        {isLinked ? (
          <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M3 8.5l3.5 3.5L13 4.5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : (
          <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
            <path d="M6.5 9.5l3-3M5 11l-1.5 1.5a2.12 2.12 0 01-3-3L2 8m9-3l1.5-1.5a2.12 2.12 0 013 3L14 8" strokeLinecap="round" strokeLinejoin="round" transform="translate(0.5 0)" />
          </svg>
        )}
        {isLinked ? t("repoFilePanel.removeFromTask") : t("repoFilePanel.addToTask")}
      </button>
      <button
        type="button"
        onClick={() => setFullscreen((f) => !f)}
        title={fullscreen ? t("repoFilePanel.exitExpand") : t("repoFilePanel.expand")}
        className="rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
      >
        {fullscreen ? (
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M8 3v3a2 2 0 01-2 2H3m18 0h-3a2 2 0 01-2-2V3m0 18v-3a2 2 0 012-2h3M3 16h3a2 2 0 012 2v3" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : (
          <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        )}
      </button>
    </div>
  );

  return (
    <div className={cn("flex h-[70vh] flex-col xl:h-[calc(100vh-7rem)]", className)}>
      {/* Browse view: search + tree. Kept mounted while the viewer is open so
          LazyFileTree's expansion/cache state survives the round trip. */}
      <div className={cn("flex min-h-0 flex-1 flex-col gap-3", view === "viewer" && "hidden")}>
        <div className="relative shrink-0">
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            className="pointer-events-none absolute left-3.5 top-1/2 -translate-y-1/2 text-gray-400"
          >
            <circle cx="11" cy="11" r="8" />
            <path d="M21 21l-4.35-4.35" strokeLinecap="round" />
          </svg>
          <input
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") e.preventDefault();
            }}
            placeholder={t("repoFilePanel.searchPlaceholder")}
            className={cn(
              "w-full rounded-xl py-2.5 pl-9 pr-4 text-sm text-gray-100 placeholder:text-gray-500",
              "bg-white/[0.03] backdrop-blur-sm",
              "border border-white/[0.06]",
              "focus:bg-white/[0.06] focus:border-primary-500/50 focus:outline-none",
              "focus:shadow-[0_0_0_3px_rgba(59,130,246,0.1)]"
            )}
          />
        </div>

        <GlassCard padding="none" className="min-h-0 flex-1 overflow-y-auto">
          {/* Tree stays mounted behind search results to preserve loaded levels. */}
          <div className={cn(query !== "" && "hidden")}>
            <LazyFileTree
              projectId={projectId}
              repoId={repo.id}
              defaultBranch={repo.defaultBranch}
              selectedPath={selectedPath}
              onFileSelect={openFile}
              linkedPaths={new Set(linkedFiles)}
            />
          </div>

          {query !== "" && (
            <div className="py-1">
              {isSearching ? (
                <div className="px-3 py-4 text-sm text-gray-400">
                  {t("repoFilePanel.searching")}
                </div>
              ) : results.length === 0 ? (
                <div className="px-3 py-4 text-sm text-gray-400">
                  {t("repoFilePanel.noMatches")}
                </div>
              ) : (
                results.map((path) => {
                  const linked = linkedFiles.includes(path);
                  return (
                    <button
                      key={path}
                      type="button"
                      onClick={() => openFile(path)}
                      className="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm text-gray-300 transition-colors hover:bg-white/[0.04]"
                    >
                      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" className="shrink-0 text-gray-400">
                        <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
                        <polyline points="14 2 14 8 20 8" strokeLinecap="round" strokeLinejoin="round" />
                      </svg>
                      <span className="min-w-0 flex-1 truncate font-mono text-sm">{path}</span>
                      {linked && (
                        <span className="flex shrink-0 items-center gap-1 text-xs text-primary-300">
                          <svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                            <path d="M3 8.5l3.5 3.5L13 4.5" strokeLinecap="round" strokeLinejoin="round" />
                          </svg>
                          {t("repoFilePanel.linked")}
                        </span>
                      )}
                    </button>
                  );
                })
              )}
            </div>
          )}
        </GlassCard>
      </div>

      {/* Viewer view: the code takes over the entire panel. */}
      {view === "viewer" && (
        <div
          className={cn(
            "flex min-h-0 flex-1 flex-col",
            fullscreen && "fixed inset-0 z-50 bg-gray-950/95 p-4 backdrop-blur-xl lg:p-8"
          )}
        >
          <button
            type="button"
            onClick={() => {
              setFullscreen(false);
              setView("browse");
            }}
            className="mb-2 flex shrink-0 items-center gap-1.5 text-sm text-gray-400 transition-colors hover:text-gray-200"
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M15 18l-6-6 6-6" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            {t("repoFilePanel.backToFiles")}
          </button>

          {contentError ? (
            <GlassCard padding="lg" className="flex flex-1 items-center justify-center">
              <div className="text-center">
                <p className="mb-2 text-sm text-red-400">{t("repoFilePanel.loadError")}</p>
                <button
                  type="button"
                  onClick={() => selectedPath && openFile(selectedPath)}
                  className="text-sm text-gray-400 underline transition-colors hover:text-gray-200"
                >
                  {t("repoFilePanel.retry")}
                </button>
              </div>
            </GlassCard>
          ) : (
            <FileContentViewer
              content={content}
              filePath={selectedPath}
              loading={loadingContent}
              actions={viewerActions}
            />
          )}
        </div>
      )}
    </div>
  );
}

export { RepoFilePickerPanel };
