import { useState } from "react";
import { useTranslations } from "use-intl";
import { GlassCard, Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import type { ProjectRepo } from "@/lib/types";
import { LocalFolderPickerModal } from "@/components/repos/LocalFolderPickerModal";
import { LocalRepoRemotePanel } from "./LocalRepoRemotePanel";
import { refreshData } from "@/lib/data-refresh";

/**
 * The "I don't have this on a forge yet" entry point.
 *
 * Sits beside "Add repository by URL" and answers the other direction: the code already
 * exists as a folder on the developer's machine, and the repository — if there is ever going
 * to be one — comes later. Linking git-inits the folder if needed, so "a folder of files" is
 * a valid starting point rather than something to fix in a terminal first.
 *
 * Only meaningful with a connected runner; the panel inside each row hides itself when no runner
 * answers, which is the same condition that makes the whole flow unusable.
 */
export function LocalFolderConnect({
  projectId,
  repos,
}: {
  projectId: string;
  repos: ProjectRepo[];
}) {
  const t = useTranslations("dashboard.localRepos");
  const [pickerOpen, setPickerOpen] = useState(false);

  const localRepos = repos.filter((r) => r.provider === "local");

  return (
    <GlassCard className="p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="text-sm font-medium text-gray-100">
            {t("addLocalRepository")}
          </h3>
          <p className="mt-1 text-xs text-gray-400">{t("hostedHint")}</p>
        </div>

        <RequirePermission permission="ReposManage">
          <Button size="sm" onClick={() => setPickerOpen(true)}>
            {t("browse")}
          </Button>
        </RequirePermission>
      </div>

      {localRepos.length > 0 && (
        <div className="mt-4 space-y-3">
          {localRepos.map((repo) => (
            <div
              key={repo.id}
              className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-3"
            >
              <div className="flex items-center gap-2">
                <span className="truncate text-sm text-gray-200">{repo.name}</span>
                <span className="truncate font-mono text-xs text-gray-400">
                  {repo.gitUrl}
                </span>
              </div>
              <div className="mt-2">
                <LocalRepoRemotePanel
                  projectId={projectId}
                  repoId={repo.id}
                  repoName={repo.name}
                />
              </div>
            </div>
          ))}
        </div>
      )}

      <LocalFolderPickerModal
        open={pickerOpen}
        onClose={() => setPickerOpen(false)}
        projectId={projectId}
        onLinked={() => refreshData()}
      />
    </GlassCard>
  );
}
