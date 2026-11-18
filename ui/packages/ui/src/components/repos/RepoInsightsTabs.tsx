import { useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { CommitList } from "./CommitList";
import { BranchList } from "./BranchList";
import { PrList } from "./PrList";

type InsightsTab = "code" | "commits" | "branches" | "pulls";

const TABS: { key: InsightsTab; labelKey: "tabCode" | "tabCommits" | "tabBranches" | "tabPulls" }[] = [
  { key: "code", labelKey: "tabCode" },
  { key: "commits", labelKey: "tabCommits" },
  { key: "branches", labelKey: "tabBranches" },
  { key: "pulls", labelKey: "tabPulls" },
];

interface RepoInsightsTabsProps {
  projectId: string;
  repoId: string;
  repoName: string;
  defaultBranch: string;
  gitUrl?: string;
  /** The app's existing file browser, rendered as the Code tab. */
  codeTab: React.ReactNode;
  /** ProjectRepo.provider — "local" for a linked folder, else the forge. */
  provider?: string;
}

/**
 * Tab strip wrapping a repo's content area: Code (the app-provided file
 * browser) plus lazily mounted Commits / Branches / Pull Requests views.
 * Panels stay mounted once visited so their state (and the file browser's
 * selection) survives tab switches.
 */
function RepoInsightsTabs({
  projectId,
  repoId,
  repoName,
  defaultBranch,
  gitUrl,
  codeTab,
  provider,
}: RepoInsightsTabsProps) {
  const t = useTranslations("dashboard.repoInsights");
  const withToken = useClientApi();
  const [active, setActive] = useState<InsightsTab>("code");
  const [visited, setVisited] = useState<Record<InsightsTab, boolean>>({
    code: true,
    commits: false,
    branches: false,
    pulls: false,
  });

  // Open-PR count for the tab badge. Fetched up front (the PR list itself only
  // loads when its tab is first opened) and kept fresh by PrList's own loads.
  const [openPrCount, setOpenPrCount] = useState<number | null>(null);
  useEffect(() => {
    let cancelled = false;
    withToken((token) => api.repoPulls.list(repoId, "open", token))
      .then((data) => {
        if (!cancelled) setOpenPrCount(data.length);
      })
      .catch(() => {
        // Badge is decorative — a failed count (e.g. runner offline) just hides it.
      });
    return () => {
      cancelled = true;
    };
  }, [repoId, withToken]);

  function activate(tab: InsightsTab) {
    setActive(tab);
    setVisited((prev) => (prev[tab] ? prev : { ...prev, [tab]: true }));
  }

  return (
    <div className="flex flex-1 flex-col min-h-0">
      {/* Tab strip */}
      <div className="mb-4 flex items-center gap-1 border-b border-white/[0.06]">
        {TABS.map((tab) => (
          <button
            key={tab.key}
            onClick={() => activate(tab.key)}
            className={cn(
              "-mb-px px-3 py-2 text-sm font-medium transition-colors border-b-2",
              active === tab.key
                ? "border-cyan-400 text-white"
                : "border-transparent text-gray-400 hover:text-gray-300"
            )}
          >
            {t(tab.labelKey)}
            {tab.key === "pulls" && openPrCount !== null && openPrCount > 0 && (
              <span
                className={cn(
                  "ml-1.5 inline-flex min-w-[18px] items-center justify-center rounded-full px-1.5 py-0.5 text-xs font-semibold leading-none",
                  active === tab.key
                    ? "bg-cyan-400/15 text-cyan-300"
                    : "bg-white/[0.08] text-gray-400"
                )}
              >
                {openPrCount}
              </span>
            )}
          </button>
        ))}
        {gitUrl?.startsWith("http") && (
          <a
            href={gitUrl}
            target="_blank"
            rel="noopener noreferrer"
            title={t("openRemoteTitle", { name: repoName })}
            className="ml-auto mb-1 flex items-center gap-1.5 rounded-lg px-2 py-1 text-xs text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
          >
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M18 13v6a2 2 0 01-2 2H5a2 2 0 01-2-2V8a2 2 0 012-2h6M15 3h6v6M10 14L21 3" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
            <span className="hidden sm:inline">{t("openRemote")}</span>
          </a>
        )}
      </div>

      {/* Code (always mounted so browser state persists) */}
      <div
        className={cn(
          active === "code" ? "flex flex-1 flex-col min-h-0" : "hidden"
        )}
      >
        {codeTab}
      </div>

      {/* Commits */}
      {visited.commits && (
        <div
          className={cn(
            active === "commits" ? "flex-1 min-h-0 overflow-y-auto" : "hidden"
          )}
        >
          <CommitList
            projectId={projectId}
            repoId={repoId}
            defaultBranch={defaultBranch}
          />
        </div>
      )}

      {/* Branches */}
      {visited.branches && (
        <div
          className={cn(
            active === "branches" ? "flex-1 min-h-0 overflow-y-auto" : "hidden"
          )}
        >
          <BranchList projectId={projectId} repoId={repoId} />
        </div>
      )}

      {/* Pull Requests */}
      {visited.pulls && (
        <div
          className={cn(
            active === "pulls" ? "flex-1 min-h-0 overflow-y-auto" : "hidden"
          )}
        >
          <PrList
            projectId={projectId}
            repoId={repoId}
            onOpenCountChange={setOpenPrCount}
            repoIsRemote={provider !== undefined && provider !== "local"}
          />
        </div>
      )}
    </div>
  );
}

export { RepoInsightsTabs };
