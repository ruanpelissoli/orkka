import { useState } from "react";
import { useTranslations } from "use-intl";
import { GlassCard, Badge, Dropdown } from "@/components/ui";
import { cn } from "@/lib/utils";
import { RepoProviderIcon } from "@/components/ui/brand-icons";

const COLLAPSE_KEY = "orkka.repohub.collapsed";

export interface RepoHubItem {
  id: string;
  name: string;
  defaultBranch: string;
  /** Small muted mono line under the name in the list (e.g. local filesystem path). */
  subtitle?: string;
  /** Shown in the detail header; an "Open remote" link renders when it starts with http. */
  gitUrl?: string;
  /** ProjectRepo.provider — picks the row's icon (forge glyph, or a folder for a local repo). */
  provider?: string;
}

interface RepoHubLayoutProps {
  repos: RepoHubItem[];
  selectedRepoId: string | null;
  onSelect: (repoId: string) => void;
  /** Add/manage affordance rendered at the top of the repo list panel. */
  addAction?: React.ReactNode;
  /** Per-repo secondary action rendered at the right edge of each list row. */
  renderRepoAction?: (repo: RepoHubItem) => React.ReactNode;
  /** Extra actions in the detail header (e.g. Remove). */
  detailActions?: React.ReactNode;
  /** Shown in the detail area when there are no repos at all. */
  emptyState?: React.ReactNode;
  /** Detail content for the selected repo (e.g. RepoInsightsTabs). */
  children: React.ReactNode;
}

/**
 * GitHub-style master–detail shell for the repo hub: a repo list panel on the
 * left (dropdown on mobile) and a detail area with a repo header on the
 * right. Purely presentational — apps own data fetching, selection state, and
 * the add/remove affordances passed in via slots.
 */
function RepoHubLayout({
  repos,
  selectedRepoId,
  onSelect,
  addAction,
  renderRepoAction,
  detailActions,
  emptyState,
  children,
}: RepoHubLayoutProps) {
  const t = useTranslations("dashboard.repoMisc.hubLayout");
  const selected = repos.find((r) => r.id === selectedRepoId) ?? null;
  const hasRepos = repos.length > 0;

  // Collapsed state persists across visits (client-rendered, so storage is
  // readable in the initializer).
  const [collapsed, setCollapsed] = useState(() => {
    try {
      return localStorage.getItem(COLLAPSE_KEY) === "1";
    } catch {
      return false; // storage unavailable — stay expanded
    }
  });

  function toggleCollapsed() {
    setCollapsed((value) => {
      const next = !value;
      try {
        localStorage.setItem(COLLAPSE_KEY, next ? "1" : "0");
      } catch {
        // best effort
      }
      return next;
    });
  }

  return (
    <div className="flex flex-1 min-h-0 flex-col gap-3 md:flex-row md:gap-4">
      {/* Mobile: dropdown selector + add action */}
      {hasRepos && (
        <div className="flex items-center gap-2 md:hidden">
          <Dropdown
            className="flex-1 min-w-0"
            trigger={
              <button className="flex w-full items-center justify-between gap-2 rounded-xl border border-white/[0.08] bg-white/[0.03] px-4 py-2.5 text-sm transition-colors hover:bg-white/[0.06]">
                <span className="flex items-center gap-2 min-w-0">
                  <RepoProviderIcon provider={selected?.provider} className="shrink-0 text-gray-400" />
                  {selected ? (
                    <>
                      <span className="truncate text-white">{selected.name}</span>
                      <Badge variant="default" size="sm">{selected.defaultBranch}</Badge>
                    </>
                  ) : (
                    <span className="text-gray-400">{t("selectPlaceholder")}</span>
                  )}
                </span>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-gray-400">
                  <path d="M6 9l6 6 6-6" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </button>
            }
            items={repos.map((repo) => ({ key: repo.id, label: repo.name }))}
            onSelect={onSelect}
          />
          {addAction && <div className="shrink-0">{addAction}</div>}
        </div>
      )}

      {/* Desktop: repo list panel — collapses to an icon rail so the code view
          gets the width back */}
      <GlassCard
        padding="none"
        className={cn(
          "hidden md:flex shrink-0 flex-col overflow-hidden self-stretch transition-all duration-200",
          collapsed ? "w-14" : "w-72"
        )}
      >
        <div
          className={cn(
            "flex items-center gap-2 border-b border-white/[0.06] px-3 py-2",
            collapsed ? "justify-center px-0" : "justify-between"
          )}
        >
          {!collapsed && (
            <>
              <span className="text-label">
                Repositories
              </span>
              {addAction}
            </>
          )}
          <button
            type="button"
            onClick={toggleCollapsed}
            title={collapsed ? t("expandList") : t("collapseList")}
            className="shrink-0 rounded-md p-1 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-200"
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 16 16"
              fill="none"
              className={cn("transition-transform", collapsed && "rotate-180")}
            >
              <path d="M10 12L6 8l4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
        </div>
        <div
          className={cn(
            "flex-1 overflow-y-auto space-y-1",
            collapsed ? "p-1.5" : "p-2"
          )}
        >
          {!hasRepos ? (
            !collapsed && (
              <p className="px-2 py-4 text-center text-xs text-gray-400">
                {t("noRepos")}
              </p>
            )
          ) : collapsed ? (
            repos.map((repo) => {
              const isSelected = repo.id === selectedRepoId;
              return (
                <button
                  key={repo.id}
                  type="button"
                  onClick={() => onSelect(repo.id)}
                  title={repo.name}
                  className={cn(
                    "flex w-full items-center justify-center rounded-lg py-2.5 transition-colors border",
                    isSelected
                      ? "border-white/[0.1] bg-white/[0.07] text-gray-200"
                      : "border-transparent text-gray-400 hover:bg-white/[0.04] hover:text-gray-300"
                  )}
                >
                  <RepoProviderIcon provider={repo.provider} size={16} />
                </button>
              );
            })
          ) : (
            repos.map((repo) => {
              const isSelected = repo.id === selectedRepoId;
              return (
                <div
                  key={repo.id}
                  role="button"
                  tabIndex={0}
                  onClick={() => onSelect(repo.id)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" || e.key === " ") {
                      e.preventDefault();
                      onSelect(repo.id);
                    }
                  }}
                  className={cn(
                    "group flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left cursor-pointer transition-colors border",
                    isSelected
                      ? "border-white/[0.1] bg-white/[0.07]"
                      : "border-transparent hover:bg-white/[0.04]"
                  )}
                >
                  <RepoProviderIcon
                    provider={repo.provider}
                    className={cn(
                      "shrink-0",
                      isSelected ? "text-gray-200" : "text-gray-400"
                    )}
                  />
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2">
                      <span
                        className={cn(
                          "truncate text-sm font-medium",
                          isSelected ? "text-white" : "text-gray-300"
                        )}
                      >
                        {repo.name}
                      </span>
                      <Badge variant="default" size="sm">{repo.defaultBranch}</Badge>
                    </div>
                    {repo.subtitle && (
                      <p className="mt-0.5 truncate text-xs text-gray-400">
                        {repo.subtitle}
                      </p>
                    )}
                  </div>
                  {renderRepoAction && (
                    <div
                      className="shrink-0 opacity-0 group-hover:opacity-100 transition-opacity"
                      onClick={(e) => e.stopPropagation()}
                    >
                      {renderRepoAction(repo)}
                    </div>
                  )}
                </div>
              );
            })
          )}
        </div>
      </GlassCard>

      {/* Detail area */}
      <div className="flex min-w-0 flex-1 flex-col min-h-0">
        {!hasRepos ? (
          emptyState ?? (
            <GlassCard padding="lg" className="flex-1 flex items-center justify-center">
              <div className="text-center text-gray-400">
                <RepoProviderIcon provider="other" size={32} className="mx-auto mb-3 opacity-40" />
                <p className="text-sm">{t("noRepos")}</p>
              </div>
            </GlassCard>
          )
        ) : !selected ? (
          <GlassCard padding="lg" className="flex-1 flex items-center justify-center">
            <div className="text-center text-gray-400">
              <RepoProviderIcon provider="other" size={32} className="mx-auto mb-3 opacity-40" />
              <p className="text-sm">{t("selectToBrowse")}</p>
            </div>
          </GlassCard>
        ) : (
          <>
            {/* Detail header */}
            <div className="mb-4 flex items-center gap-2 flex-wrap min-w-0">
              <RepoProviderIcon provider={selected.provider} className="shrink-0 text-gray-400" />
              <h2 className="font-display text-lg font-semibold text-white truncate">
                {selected.name}
              </h2>
              <Badge variant="default" size="sm">{selected.defaultBranch}</Badge>
              {selected.gitUrl && (
                <span
                  className="hidden lg:block min-w-0 max-w-sm truncate font-mono text-xs text-gray-400"
                  title={selected.gitUrl}
                >
                  {selected.gitUrl}
                </span>
              )}
              <div className="ml-auto flex items-center gap-2 shrink-0">
                {selected.gitUrl?.startsWith("http") && (
                  <a
                    href={selected.gitUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="flex items-center gap-1.5 rounded-lg border border-white/[0.08] bg-white/[0.03] px-2.5 py-1 text-xs text-gray-300 transition-colors hover:bg-white/[0.06] hover:text-white"
                  >
                    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                      <path d="M18 13v6a2 2 0 01-2 2H5a2 2 0 01-2-2V8a2 2 0 012-2h6M15 3h6v6M10 14L21 3" strokeLinecap="round" strokeLinejoin="round" />
                    </svg>
                    {t("openRemote")}
                  </a>
                )}
                {detailActions}
              </div>
            </div>

            {children}
          </>
        )}
      </div>
    </div>
  );
}

export { RepoHubLayout };
