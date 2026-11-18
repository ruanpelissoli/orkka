import { useState, useEffect, useMemo } from "react";
import { useTranslations } from "use-intl";
import { Modal, Skeleton } from "@/components/ui";
import { DiffFileCard } from "@/components/pr/DiffFileCard";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { parseCommitDiff } from "./commit-diff-parser";

interface CommitDiffModalProps {
  projectId: string;
  repoId: string;
  /** Commit to show; null keeps the modal closed. */
  sha: string | null;
  onClose: () => void;
}

function CommitDiffModal({ projectId, repoId, sha, onClose }: CommitDiffModalProps) {
  const t = useTranslations("dashboard.repoMisc.commitDiff");
  const withToken = useClientApi();
  // The loaded result remembers which commit it belongs to, so "loading" is
  // derived: a result for another commit (or none yet) means still loading.
  const [result, setResult] = useState<{ key: string; diff: string | null; error: boolean } | null>(null);
  const key = sha ? `${projectId}/${repoId}/${sha}` : null;
  const current = result !== null && result.key === key ? result : null;
  const loading = key !== null && current === null;
  const diff = current?.diff ?? null;
  const error = current?.error ?? false;

  useEffect(() => {
    if (!sha) return;
    let cancelled = false;
    const requestKey = `${projectId}/${repoId}/${sha}`;

    withToken((token) => api.repoHistory.commitDiff(projectId, repoId, sha, token))
      .then((data) => {
        if (!cancelled) setResult({ key: requestKey, diff: data.diff, error: false });
      })
      .catch(() => {
        if (!cancelled) setResult({ key: requestKey, diff: null, error: true });
      });

    return () => {
      cancelled = true;
    };
  }, [sha, projectId, repoId, withToken]);

  const files = useMemo(() => (diff ? parseCommitDiff(diff) : []), [diff]);

  return (
    <Modal
      open={sha !== null}
      onClose={onClose}
      title={sha ? t("title", { sha: sha.slice(0, 7) }) : t("titleBare")}
      className="md:max-w-4xl"
    >
      {loading ? (
        <div className="space-y-3">
          <Skeleton className="h-8 w-1/2" />
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : error ? (
        <p className="py-6 text-center text-sm text-red-400">
          {t("loadFailed")}
        </p>
      ) : files.length === 0 ? (
        <p className="py-6 text-center text-sm text-gray-400">
          {t("empty")}
        </p>
      ) : (
        <div className="space-y-3">
          <div className="flex items-center gap-3 text-xs">
            <span className="text-gray-400">
              {t("filesChanged", { count: files.length })}
            </span>
            <span className="font-mono text-emerald-400">
              +{files.reduce((n, f) => n + f.additions, 0)}
            </span>
            <span className="font-mono text-red-400">
              -{files.reduce((n, f) => n + f.deletions, 0)}
            </span>
          </div>
          {files.map((file) => (
            <DiffFileCard key={file.filename} file={file} readOnly />
          ))}
        </div>
      )}
    </Modal>
  );
}

export { CommitDiffModal };
