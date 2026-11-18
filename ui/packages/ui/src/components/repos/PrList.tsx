import { useState, useEffect, useEffectEvent } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Badge, Skeleton } from "@/components/ui";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { formatRelativeTime, cn } from "@/lib/utils";
import type { RepoPrSummary } from "@/lib/types";
import { RepoPrModal } from "./RepoPrModal";

type PrStateFilter = "open" | "closed" | "all";

const FILTERS: { key: PrStateFilter; labelKey: "filterOpen" | "filterClosed" | "filterAll" }[] = [
  { key: "open", labelKey: "filterOpen" },
  { key: "closed", labelKey: "filterClosed" },
  { key: "all", labelKey: "filterAll" },
];

const EMPTY_KEYS: Record<PrStateFilter, "emptyOpen" | "emptyClosed" | "emptyAll"> = {
  open: "emptyOpen",
  closed: "emptyClosed",
  all: "emptyAll",
};

interface PrListProps {
  projectId: string;
  repoId: string;
  /** Reports the open-PR count whenever the Open filter's data loads (keeps the tab badge fresh). */
  onOpenCountChange?: (count: number) => void;
  /** See RepoPrModal. */
  repoIsRemote?: boolean;
}

function PrList({ projectId, repoId, onOpenCountChange, repoIsRemote }: PrListProps) {
  const t = useTranslations("dashboard.repoMisc.prList");
  const withToken = useClientApi();

  const [filter, setFilter] = useState<PrStateFilter>("open");
  const [pulls, setPulls] = useState<RepoPrSummary[]>([]);
  // Which list the pulls belong to; a different (or no) key means the current
  // filter's list is still loading.
  const [loaded, setLoaded] = useState<{ key: string; error: boolean } | null>(null);
  const [selectedNumber, setSelectedNumber] = useState<number | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  const listKey = `${repoId}/${filter}/${refreshKey}`;
  const loading = loaded === null || loaded.key !== listKey;
  const error = !loading && loaded.error;

  // Reads the latest callback without making it a reason to refetch.
  const reportOpenCount = useEffectEvent((count: number) => {
    onOpenCountChange?.(count);
  });

  useEffect(() => {
    let cancelled = false;
    const key = `${repoId}/${filter}/${refreshKey}`;

    withToken((token) => api.repoPulls.list(repoId, filter, token))
      .then((data) => {
        if (cancelled) return;
        setPulls(data);
        setLoaded({ key, error: false });
        if (filter === "open") reportOpenCount(data.length);
      })
      .catch(() => {
        if (!cancelled) {
          setPulls([]);
          setLoaded({ key, error: true });
        }
      });

    return () => {
      cancelled = true;
    };
  }, [repoId, filter, refreshKey, withToken]);

  return (
    <div className="space-y-3">
      {/* State filter tabs */}
      <div className="flex items-center gap-1 rounded-lg border border-white/[0.06] bg-white/[0.02] p-1 w-fit">
        {FILTERS.map((f) => (
          <button
            key={f.key}
            onClick={() => setFilter(f.key)}
            className={cn(
              "rounded-md px-3 py-1 text-xs font-medium transition-colors",
              filter === f.key
                ? "bg-white/[0.08] text-white"
                : "text-gray-400 hover:text-gray-300"
            )}
          >
            {t(f.labelKey)}
          </button>
        ))}
      </div>

      {loading ? (
        <div className="space-y-2">
          {Array.from({ length: 4 }).map((_, i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      ) : error ? (
        <p className="py-8 text-center text-sm text-red-400">
          {t("loadFailed")}
        </p>
      ) : pulls.length === 0 ? (
        <p className="py-8 text-center text-sm text-gray-400">
          {t(EMPTY_KEYS[filter])}
        </p>
      ) : (
        <div className="divide-y divide-white/[0.04] rounded-xl border border-white/[0.06] overflow-hidden">
          {pulls.map((pr) => (
            <div
              key={pr.number}
              role="button"
              tabIndex={0}
              onClick={() => setSelectedNumber(pr.number)}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  setSelectedNumber(pr.number);
                }
              }}
              className="flex w-full items-center gap-3 px-4 py-3 text-left transition-colors hover:bg-white/[0.03] cursor-pointer"
            >
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-sm text-gray-200 font-medium">
                    <span className="text-gray-400 font-normal">#{pr.number}</span>{" "}
                    {pr.title}
                  </span>
                  {pr.draft && <Badge variant="default" size="sm">{t("draft")}</Badge>}
                  <Badge
                    variant={
                      pr.merged
                        ? "purple"
                        : pr.state === "open"
                          ? "success"
                          : "error"
                    }
                    size="sm"
                    dot
                  >
                    {pr.merged ? t("merged") : pr.state === "open" ? t("open") : t("closed")}
                  </Badge>
                </div>
                <div className="mt-1 flex items-center gap-2 text-xs text-gray-400 min-w-0">
                  <span className="font-mono text-cyan-400/80 truncate max-w-[12rem]">
                    {pr.headBranch}
                  </span>
                  <span className="shrink-0">→</span>
                  <span className="font-mono truncate max-w-[8rem]">{pr.baseBranch}</span>
                  <span className="shrink-0">·</span>
                  <span className="truncate">{pr.authorLogin}</span>
                  <span className="shrink-0">·</span>
                  <span className="shrink-0">
                    {formatRelativeTime(pr.createdAt)}
                  </span>
                </div>
              </div>

              <div className="flex items-center gap-2 shrink-0">
                {pr.taskNumber !== null && (
                  <Link
                    href={`/p/${projectId}/tasks/${pr.taskNumber}`}
                    onClick={(e) => e.stopPropagation()}
                    className="rounded-lg border border-white/[0.08] bg-white/[0.03] px-2.5 py-1 text-xs text-gray-300 transition-colors hover:bg-white/[0.06] hover:text-white"
                  >
                    {t("viewTask")}
                  </Link>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      <RepoPrModal
        repoId={repoId}
        number={selectedNumber}
        onClose={() => setSelectedNumber(null)}
        onChanged={() => setRefreshKey((k) => k + 1)}
        repoIsRemote={repoIsRemote}
      />
    </div>
  );
}

export { PrList };
