import { useState, useEffect, useRef, useCallback, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button, GlassCard, Skeleton } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { api } from "@/lib/api";
import { createTaskPr } from "@/lib/actions";
import { useClientApi } from "@/hooks/use-client-api";
import { PrHeader } from "./PrHeader";
import { PrFileList } from "./PrFileList";
import { DiffViewer } from "./DiffViewer";
import { PrReviewHistory } from "./PrReviewHistory";
import { PrReviewActions } from "./PrReviewActions";
import type { PullRequestDetail, PrComment } from "@/lib/types";

interface PrReviewPanelProps {
  taskId: string;
  /** See PrReviewActions — gates the approve controls on WaitingCodeReview. */
  taskStatus?: import("@/lib/constants").TaskStatusKey;
  projectId: string;
  onRefresh?: () => void;
  /** See PrHeader — a remote repo's work is published as a forge PR, not merged. */
  repoIsRemote?: boolean;
}

function PrReviewPanel({ taskId, taskStatus, projectId, onRefresh, repoIsRemote }: PrReviewPanelProps) {
  const t = useTranslations("dashboard.prReview");
  const [pr, setPr] = useState<PullRequestDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [activeFile, setActiveFile] = useState<string | null>(null);
  const fileRefs = useRef<Map<string, HTMLDivElement>>(new Map());

  const withToken = useClientApi();

  const fetchPr = useCallback(
    () =>
      withToken((token) => api.pullRequests.get(taskId, token))
        .catch(() => null)
        .then((data) => {
          setPr(data);
          setLoading(false);
        }),
    [taskId, withToken]
  );

  useEffect(() => {
    fetchPr();
  }, [fetchPr]);

  const handleFileClick = useCallback((filename: string) => {
    setActiveFile(filename);
    const el = fileRefs.current.get(filename);
    if (el) {
      el.scrollIntoView({ behavior: "smooth", block: "start" });
    }
  }, []);

  const handleCommentAdded = useCallback((comment: PrComment) => {
    setPr((prev) => {
      if (!prev) return prev;
      return { ...prev, comments: [...prev.comments, comment] };
    });
  }, []);

  const handleReviewSubmitted = useCallback(() => {
    fetchPr();
    onRefresh?.();
  }, [fetchPr, onRefresh]);

  // "Task in code review with no PR" recovery: the automatic creation failed (or is still
  // pending) — let the user open the PR by hand instead of leaving a dead panel.
  const [isCreatingPr, startCreatePr] = useTransition();
  const [createPrError, setCreatePrError] = useState<string | null>(null);
  const handleCreatePr = useCallback(() => {
    startCreatePr(async () => {
      const res = await createTaskPr(taskId);
      if (res.error) {
        setCreatePrError(res.error);
        return;
      }
      setCreatePrError(null);
      setLoading(true);
      await fetchPr();
      onRefresh?.();
    });
  }, [taskId, fetchPr, onRefresh]);

  const handleMerged = useCallback(() => {
    fetchPr();
    onRefresh?.();
  }, [fetchPr, onRefresh]);

  // Expose refresh for SignalR
  const handlePrRefresh = useCallback(() => {
    fetchPr();
  }, [fetchPr]);

  // Store refresh handler on the DOM element so parent can call it
  const panelRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = panelRef.current;
    if (el) {
      (el as HTMLDivElement & { __prRefresh?: () => void }).__prRefresh = handlePrRefresh;
    }
  }, [handlePrRefresh]);

  if (loading) {
    return (
      <GlassCard padding="lg" accentColor="#06b6d4">
        <div className="space-y-4">
          <Skeleton className="h-6 w-2/3" />
          <Skeleton className="h-4 w-1/3" />
          <div className="space-y-2">
            <Skeleton className="h-20" />
            <Skeleton className="h-20" />
          </div>
        </div>
      </GlassCard>
    );
  }

  if (!pr) {
    return (
      <GlassCard padding="lg" accentColor="#06b6d4">
        <div className="text-center py-6 space-y-3">
          <p className="text-sm text-gray-400">{t("noPrYet")}</p>
          <div className="flex items-center justify-center gap-2">
            <RequirePermission permission="TasksExecute">
              <Button
                variant="primary"
                size="sm"
                loading={isCreatingPr}
                disabled={isCreatingPr}
                onClick={handleCreatePr}
              >
                {t("createPr")}
              </Button>
            </RequirePermission>
            <Button variant="ghost" size="sm" onClick={() => { setLoading(true); fetchPr(); }}>
              {t("checkAgain")}
            </Button>
          </div>
          {createPrError && (
            <p className="text-xs rounded-md border border-red-500/30 bg-red-500/10 px-3 py-2 text-red-300 inline-block">
              {createPrError}
            </p>
          )}
        </div>
      </GlassCard>
    );
  }

  return (
    <div ref={panelRef} data-pr-panel>
      <GlassCard padding="lg" accentColor="#06b6d4">
        <div className="space-y-4">
          {/* Header with PR metadata */}
          <PrHeader
            pr={pr}
            taskId={taskId}
            onMerged={handleMerged}
            repoIsRemote={repoIsRemote}
          />

          {/* File list */}
          <PrFileList
            files={pr.files}
            activeFile={activeFile}
            onFileClick={handleFileClick}
          />

          {/* Reviews history */}
          <PrReviewHistory reviews={pr.reviews} />

          {/* Diff viewer */}
          <DiffViewer
            files={pr.files}
            taskId={taskId}
            comments={pr.comments}
            onCommentAdded={handleCommentAdded}
            fileRefs={fileRefs}
          />

          {/* Review actions */}
          <PrReviewActions
            pr={pr}
            taskStatus={taskStatus}
            taskId={taskId}
            projectId={projectId}
            onReviewSubmitted={handleReviewSubmitted}
          />
        </div>
      </GlassCard>
    </div>
  );
}

export { PrReviewPanel };
