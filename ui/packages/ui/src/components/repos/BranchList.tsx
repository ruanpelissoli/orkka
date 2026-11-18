import { useState, useEffect } from "react";
import { useTranslations } from "use-intl";
import { Badge, Skeleton } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { formatRelativeTime } from "@/lib/utils";
import type { RepoBranch } from "@/lib/types";

interface BranchListProps {
  projectId: string;
  repoId: string;
}

/** The repo's branches, read-only: branches are made in the developer's own clone, not here. */
function BranchList({ projectId, repoId }: BranchListProps) {
  const t = useTranslations("dashboard.branchList");
  const withToken = useClientApi();
  const [branches, setBranches] = useState<RepoBranch[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  // Mounted per repo (the insights tabs are keyed by repo id), so the initial loading state is
  // the only one this needs — the effect only ever settles it.
  useEffect(() => {
    let cancelled = false;
    withToken((token) => api.repoHistory.branches(projectId, repoId, token))
      .then((data) => {
        if (!cancelled) setBranches(data);
      })
      .catch(() => {
        if (!cancelled) setError(true);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, repoId, withToken]);

  if (loading) {
    return (
      <div className="space-y-2">
        {Array.from({ length: 4 }).map((_, i) => (
          <Skeleton key={i} className="h-11" />
        ))}
      </div>
    );
  }

  if (error) {
    return (
      <p className="py-8 text-center text-sm text-red-400">
        {t("loadFailed")}
      </p>
    );
  }

  return (
    <div className="space-y-3">
      {branches.length === 0 ? (
        <p className="py-8 text-center text-sm text-gray-400">
          {t("empty")}
        </p>
      ) : (
        <div className="divide-y divide-white/[0.04] rounded-xl border border-white/[0.06] overflow-hidden">
          {branches.map((branch) => (
            <div
              key={branch.name}
              className="flex items-center gap-3 px-4 py-2.5"
            >
              <span className="flex items-center gap-2 min-w-0 shrink-0 max-w-[40%]">
                <span className="font-mono text-xs text-cyan-400 truncate">
                  {branch.name}
                </span>
                {branch.isDefault && (
                  <Badge variant="info" size="sm">{t("defaultBadge")}</Badge>
                )}
                {branch.isRemote && (
                  <Badge
                    variant="warning"
                    size="sm"
                    title={t("remoteTitle")}
                  >
                    {t("remoteBadge")}
                  </Badge>
                )}
              </span>
              <span className="font-mono text-xs text-gray-400 shrink-0">
                {branch.sha.slice(0, 7)}
              </span>
              <span className="flex-1 min-w-0 truncate text-sm text-gray-400">
                {branch.lastCommitSubject}
              </span>
              <span className="shrink-0 text-xs text-gray-400">
                {formatRelativeTime(branch.lastCommitDate)}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export { BranchList };
