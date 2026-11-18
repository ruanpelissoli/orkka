import { useState, useCallback } from "react";
import { useAuth } from "@/lib/local-auth-client";
import { useTranslations } from "use-intl";
import { GlassCard, Badge, Modal, Button } from "@/components/ui";
import { DefaultBranchPicker } from "@/components/repos/DefaultBranchPicker";
import { FileContentViewer } from "@/components/repos/FileContentViewer";
import { LazyFileTree } from "@/components/repos/LazyFileTree";
import { RepoInsightsTabs } from "@/components/repos/RepoInsightsTabs";
import { RepoHubLayout } from "@/components/repos/RepoHubLayout";
import { RepoProviderIcon } from "@/components/ui/brand-icons";
import { OpenInEditorButton } from "@/components/task/OpenInEditorButton";
import { api } from "@/lib/api";
import type { ProjectRepo } from "@/lib/types";
import { AddRepoByUrlButton, AddRepoByUrlPanel } from "./AddRepoByUrlPanel";
import { RepoAccessCheckButton } from "./RepoAccessCheckButton";
import { RepoDeleteButton } from "./RepoDeleteButton";
import { RepoCloneButton } from "./RepoCloneButton";
import { RepoSetupCommandButton } from "./RepoSetupCommandButton";
import { refreshData } from "@/lib/data-refresh";

interface RepoFileBrowserProps {
  projectId: string;
  repos: ProjectRepo[];
}

export function RepoFileBrowser({ projectId, repos }: RepoFileBrowserProps) {
  const t = useTranslations("dashboard.repoBrowser");
  const tFilePanel = useTranslations("dashboard.repoFilePanel");
  const { getToken } = useAuth();

  const [selectedRepoId, setSelectedRepoId] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<string | null>(null);
  const [fileContent, setFileContent] = useState<string | null>(null);
  const [loadingContent, setLoadingContent] = useState(false);

  const [manageOpen, setManageOpen] = useState(false);
  const [mobileView, setMobileView] = useState<"tree" | "content">("tree");

  const selectedRepo = repos.find((r) => r.id === selectedRepoId) ?? null;

  const handleSelectRepo = useCallback((repoId: string) => {
    setSelectedRepoId(repoId);
    setSelectedFile(null);
    setFileContent(null);
    setMobileView("tree");
  }, []);

  // Auto-select the first repo, and recover when the selected repo disappears
  // (e.g. removed via the manage modal) — adjusted during render, same reset as
  // handleSelectRepo.
  if (repos.length > 0 && (!selectedRepoId || !repos.some((r) => r.id === selectedRepoId))) {
    setSelectedRepoId(repos[0].id);
    setSelectedFile(null);
    setFileContent(null);
    setMobileView("tree");
  }

  const handleSelectFile = useCallback(async (path: string) => {
    if (!selectedRepoId) return;
    setSelectedFile(path);
    setLoadingContent(true);
    setMobileView("content");

    try {
      const token = await getToken();
      const content = await api.repos.getFileContent(projectId, selectedRepoId, path, token);
      setFileContent(content);
    } catch {
      setFileContent(null);
    } finally {
      setLoadingContent(false);
    }
  }, [getToken, projectId, selectedRepoId]);

  const manageButton = (
    <Button variant="ghost" size="sm" onClick={() => setManageOpen(true)}>
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.65 1.65 0 00.33 1.82l.06.06a2 2 0 010 2.83 2 2 0 01-2.83 0l-.06-.06a1.65 1.65 0 00-1.82-.33 1.65 1.65 0 00-1 1.51V21a2 2 0 01-4 0v-.09A1.65 1.65 0 009 19.4a1.65 1.65 0 00-1.82.33l-.06.06a2 2 0 01-2.83 0 2 2 0 010-2.83l.06-.06A1.65 1.65 0 004.68 15a1.65 1.65 0 00-1.51-1H3a2 2 0 010-4h.09A1.65 1.65 0 004.6 9a1.65 1.65 0 00-.33-1.82l-.06-.06a2 2 0 112.83-2.83l.06.06A1.65 1.65 0 009 4.68a1.65 1.65 0 001-1.51V3a2 2 0 014 0v.09a1.65 1.65 0 001 1.51 1.65 1.65 0 001.82-.33l.06-.06a2 2 0 012.83 2.83l-.06.06A1.65 1.65 0 0019.4 9a1.65 1.65 0 001.51 1H21a2 2 0 010 4h-.09a1.65 1.65 0 00-1.51 1z" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      {t("manage")}
    </Button>
  );

  return (
    <div className="flex h-full flex-col">
      {/* Header */}
      <div className="flex items-center justify-between mb-4">
        <h1 className="font-display text-xl font-bold text-white">
          {t("title")}
        </h1>
      </div>

      <RepoHubLayout
        repos={repos.map((repo) => ({
          id: repo.id,
          name: repo.name,
          defaultBranch: repo.defaultBranch,
          gitUrl: repo.gitUrl,
          provider: repo.provider,
        }))}
        selectedRepoId={selectedRepoId}
        onSelect={handleSelectRepo}
        addAction={manageButton}
        detailActions={
          selectedRepo && (
            <>
              <DefaultBranchPicker
                key={`branch-${selectedRepo.id}`}
                projectId={projectId}
                repoId={selectedRepo.id}
                defaultBranch={selectedRepo.defaultBranch}
                onChanged={() => refreshData()}
              />
              {/* Renders only when the caller's own runner is online with an editor. */}
              <OpenInEditorButton
                key={`editor-${selectedRepo.id}`}
                target={{ kind: "repo", projectId, repoId: selectedRepo.id }}
                compact
              />
            </>
          )
        }
        emptyState={
          <GlassCard padding="lg" className="flex-1 flex items-center justify-center">
            <div className="text-center text-gray-400">
              <RepoProviderIcon provider="other" size={32} className="mx-auto mb-3 opacity-40" />
              <p className="text-sm mb-4">
                {t("emptyHint")}
              </p>
              <div className="flex justify-center">
                <AddRepoByUrlButton projectId={projectId} />
              </div>
            </div>
          </GlassCard>
        }
      >
        {selectedRepo && (
          <RepoInsightsTabs
            key={selectedRepo.id}
            projectId={projectId}
            repoId={selectedRepo.id}
            repoName={selectedRepo.name}
            defaultBranch={selectedRepo.defaultBranch}
            provider={selectedRepo.provider}
            codeTab={
              <>
                {/* Desktop: side by side */}
                <div className="hidden md:flex flex-1 gap-3 min-h-0">
                  <GlassCard padding="none" className="w-80 shrink-0 overflow-y-auto">
                    <LazyFileTree
                      projectId={projectId}
                      repoId={selectedRepo.id}
                      defaultBranch={selectedRepo.defaultBranch}
                      selectedPath={selectedFile}
                      onFileSelect={handleSelectFile}
                    />
                  </GlassCard>
                  <FileContentViewer
                    content={fileContent}
                    filePath={selectedFile}
                    loading={loadingContent}
                  />
                </div>

                {/* Mobile: toggle between tree and content */}
                <div className="flex md:hidden flex-1 flex-col min-h-0">
                  {mobileView === "tree" ? (
                    <GlassCard padding="none" className="flex-1 overflow-y-auto">
                      <LazyFileTree
                        projectId={projectId}
                        repoId={selectedRepo.id}
                        defaultBranch={selectedRepo.defaultBranch}
                        selectedPath={selectedFile}
                        onFileSelect={handleSelectFile}
                      />
                    </GlassCard>
                  ) : (
                    <div className="flex flex-1 flex-col min-h-0">
                      <button
                        onClick={() => setMobileView("tree")}
                        className="flex items-center gap-1.5 mb-2 text-sm text-gray-400 hover:text-gray-200 transition-colors"
                      >
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                          <path d="M15 18l-6-6 6-6" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                        {tFilePanel("backToFiles")}
                      </button>
                      <FileContentViewer
                        content={fileContent}
                        filePath={selectedFile}
                        loading={loadingContent}
                      />
                    </div>
                  )}
                </div>
              </>
            }
          />
        )}
      </RepoHubLayout>

      {/* Manage Repos Modal */}
      <Modal open={manageOpen} onClose={() => setManageOpen(false)} title={t("manageTitle")}>
        <div className="space-y-3 mb-4">
          {repos.length === 0 && (
            <p className="text-sm text-gray-400">{t("noRepos")}</p>
          )}
          {repos.map((repo) => (
            <div
              key={repo.id}
              className="flex items-center justify-between rounded-xl border border-white/[0.06] bg-white/[0.02] px-4 py-3"
            >
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <RepoProviderIcon provider={repo.provider} className="shrink-0 text-gray-400" />
                  <span className="text-sm font-medium text-white truncate">{repo.name}</span>
                  <Badge variant="default" size="sm">{repo.defaultBranch}</Badge>
                </div>
                <p className="mt-0.5 text-xs text-gray-400 truncate">
                  {repo.gitUrl}
                  {repo.cliAccount && (
                    <span className="ml-1.5">· {repo.cliAccount}</span>
                  )}
                </p>
              </div>
              <div className="flex items-center gap-2 ml-3">
                <Badge
                  variant={repo.workspaceStatus === "active" ? "success" : "warning"}
                  size="sm"
                >
                  {repo.workspaceStatus}
                </Badge>
                <RepoCloneButton
                  projectId={projectId}
                  repoId={repo.id}
                  repoName={repo.name}
                />
                <RepoSetupCommandButton projectId={projectId} repo={repo} />
                <RepoDeleteButton repoId={repo.id} />
              </div>
            </div>
          ))}
        </div>
        {repos.some((r) => r.provider !== "local") && (
          <div className="mb-4">
            <RepoAccessCheckButton projectId={projectId} />
          </div>
        )}
        <div className="border-t border-white/[0.06] pt-4">
          <AddRepoByUrlPanel projectId={projectId} />
        </div>
      </Modal>
    </div>
  );
}
