import { useState, useEffect, useCallback } from "react";
import { useTranslations } from "use-intl";
import { Button, Dropdown, Skeleton } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { formatRelativeTime } from "@/lib/utils";
import type { RepoCommit, RepoBranch } from "@/lib/types";
import { CommitDiffModal } from "./CommitDiffModal";

const PAGE_SIZE = 50;

interface CommitListProps {
  projectId: string;
  repoId: string;
  defaultBranch: string;
}

function CommitList({ projectId, repoId, defaultBranch }: CommitListProps) {
  const t = useTranslations("dashboard.repoMisc.commitList");
  const withToken = useClientApi();

  const [branch, setBranch] = useState(defaultBranch);
  const [branches, setBranches] = useState<RepoBranch[]>([]);
  const [commits, setCommits] = useState<RepoCommit[]>([]);
  // Which list the commits belong to; a different (or no) key means the list
  // for the current branch is still loading.
  const [loaded, setLoaded] = useState<{ key: string; error: boolean } | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [hasMore, setHasMore] = useState(false);
  const [selectedSha, setSelectedSha] = useState<string | null>(null);

  useEffect(() => {
    withToken((token) => api.repoHistory.branches(projectId, repoId, token))
      .then(setBranches)
      .catch(() => setBranches([]));
  }, [projectId, repoId, withToken]);

  const fetchPage = useCallback(
    async (targetBranch: string, skip: number) => {
      const page = await withToken((token) =>
        api.repoHistory.commits(
          projectId,
          repoId,
          { branch: targetBranch, skip, take: PAGE_SIZE },
          token
        )
      );
      return page;
    },
    [projectId, repoId, withToken]
  );

  const listKey = `${projectId}/${repoId}/${branch}`;
  const loading = loaded === null || loaded.key !== listKey;
  const error = !loading && loaded.error;

  useEffect(() => {
    let cancelled = false;
    const key = `${projectId}/${repoId}/${branch}`;
    fetchPage(branch, 0)
      .then((page) => {
        if (!cancelled) {
          setCommits(page);
          setHasMore(page.length === PAGE_SIZE);
          setLoaded({ key, error: false });
        }
      })
      .catch(() => {
        if (!cancelled) {
          setCommits([]);
          setLoaded({ key, error: true });
        }
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, repoId, branch, fetchPage]);

  const handleLoadMore = useCallback(async () => {
    setLoadingMore(true);
    try {
      const page = await fetchPage(branch, commits.length);
      setHasMore(page.length === PAGE_SIZE);
      setCommits((prev) => [...prev, ...page]);
    } catch {
      // keep current list
    } finally {
      setLoadingMore(false);
    }
  }, [branch, commits.length, fetchPage]);

  return (
    <div className="space-y-3">
      {/* Branch selector */}
      <div className="flex items-center gap-2">
        <Dropdown
          trigger={
            <button className="flex items-center gap-2 rounded-lg border border-white/[0.08] bg-white/[0.03] px-3 py-1.5 text-xs transition-colors hover:bg-white/[0.06]">
              <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5" className="text-gray-400">
                <circle cx="4" cy="4" r="1.75" />
                <circle cx="4" cy="12" r="1.75" />
                <circle cx="12" cy="7" r="1.75" />
                <path d="M4 6v4M12 9c0 2-2 3-4 3" strokeLinecap="round" />
              </svg>
              <span className="text-gray-200">{branch}</span>
              <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="text-gray-400">
                <path d="M6 9l6 6 6-6" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </button>
          }
          items={
            branches.length > 0
              ? branches.map((b) => ({ key: b.name, label: b.name }))
              : [{ key: branch, label: branch }]
          }
          onSelect={(name) => setBranch(name)}
        />
      </div>

      {/* Commit rows */}
      {loading ? (
        <div className="space-y-2">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-11" />
          ))}
        </div>
      ) : error ? (
        <p className="py-8 text-center text-sm text-red-400">
          {t("loadFailed")}
        </p>
      ) : commits.length === 0 ? (
        <p className="py-8 text-center text-sm text-gray-400">
          {t("empty")}
        </p>
      ) : (
        <div className="divide-y divide-white/[0.04] rounded-xl border border-white/[0.06] overflow-hidden">
          {commits.map((commit) => (
            <button
              key={commit.sha}
              onClick={() => setSelectedSha(commit.sha)}
              className="flex w-full items-center gap-3 px-4 py-2.5 text-left transition-colors hover:bg-white/[0.03]"
            >
              <span className="font-mono text-xs text-cyan-400 shrink-0">
                {commit.sha.slice(0, 7)}
              </span>
              <span className="flex-1 min-w-0 truncate text-sm text-gray-200">
                {commit.subject}
              </span>
              <span className="hidden sm:block shrink-0 text-xs text-gray-400 truncate max-w-[10rem]">
                {commit.author}
              </span>
              <span className="shrink-0 text-xs font-mono text-gray-400">
                {formatRelativeTime(commit.date)}
              </span>
            </button>
          ))}
        </div>
      )}

      {/* Pagination */}
      {!loading && !error && hasMore && (
        <div className="flex justify-center pt-1">
          <Button
            variant="ghost"
            size="sm"
            disabled={loadingMore}
            loading={loadingMore}
            onClick={handleLoadMore}
          >
            {t("loadMore")}
          </Button>
        </div>
      )}

      <CommitDiffModal
        projectId={projectId}
        repoId={repoId}
        sha={selectedSha}
        onClose={() => setSelectedSha(null)}
      />
    </div>
  );
}

export { CommitList };
