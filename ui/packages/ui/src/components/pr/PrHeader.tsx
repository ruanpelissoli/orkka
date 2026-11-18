import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Badge, Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { mergePr, syncPr } from "@/lib/actions";
import type { PullRequestDetail } from "@/lib/types";
import { ForgePrLink } from "./ForgePrLink";

interface PrHeaderProps {
  pr: PullRequestDetail;
  taskId: string;
  onMerged?: () => void;
  /**
   * The task's repo is a remote (not a linked folder). Landing the work then PUBLISHES it:
   * the developer's runner opens a pull request on the forge with their own CLI — Orkka never
   * merges there — so the action and the landed state are worded that way.
   */
  repoIsRemote?: boolean;
}

function PrHeader({ pr, taskId, onMerged, repoIsRemote = false }: PrHeaderProps) {
  const t = useTranslations("dashboard.prReview");
  const tForge = useTranslations("dashboard.forgePr");
  const tc = useTranslations("common");
  const [isPending, startTransition] = useTransition();
  const [showMergeConfirm, setShowMergeConfirm] = useState(false);

  const isApproved = pr.reviews.some((r) => r.state === "APPROVED");
  const canMerge = pr.mergeable && pr.state === "open";
  const [notice, setNotice] = useState<{ kind: "info" | "error"; text: string } | null>(null);

  function handleMerge() {
    startTransition(async () => {
      const res = await mergePr(taskId);
      if (res.error) {
        setNotice({ kind: "error", text: res.error });
        return;
      }
      setShowMergeConfirm(false);
      setNotice(res.resolutionStarted
        ? { kind: "info", text: res.message ?? t("mergeConflictsResolving") }
        : null);
      onMerged?.();
    });
  }

  function handleSync() {
    startTransition(async () => {
      const res = await syncPr(taskId);
      if (res.error) {
        setNotice({ kind: "error", text: res.error });
        return;
      }
      setNotice({ kind: "info", text: res.result!.message });
      onMerged?.();
    });
  }

  return (
    <div className="space-y-3">
      {/* Title row */}
      <div className="flex items-start gap-3 flex-wrap">
        <h3 className="font-display text-base font-semibold text-white flex-1 min-w-0">
          {pr.title}
          <span className="text-gray-400 font-normal ml-2">#{pr.number}</span>
        </h3>
        <div className="flex items-center gap-2 shrink-0">
          {pr.draft && (
            <Badge variant="default" size="sm">{t("draft")}</Badge>
          )}
          <Badge
            variant={pr.state === "merged" ? "info" : pr.state === "open" ? "success" : "error"}
            size="sm"
            dot
          >
            {pr.state === "merged"
              ? repoIsRemote
                ? tForge("published")
                : t("stateMerged")
              : pr.state === "open"
                ? t("stateOpen")
                : t("stateClosed")}
          </Badge>
          {pr.forgeUrl && <ForgePrLink url={pr.forgeUrl} />}
          <RequirePermission permission="CodeReviewMerge">
            <Button
              variant="ghost"
              size="sm"
              disabled={isPending}
              onClick={handleSync}
              title={t("syncFromForgeHint")}
            >
              {t("syncFromForge")}
            </Button>
          </RequirePermission>
        </div>
      </div>

      {notice && (
        <p
          className={
            notice.kind === "error"
              ? "text-xs rounded-md border border-red-500/30 bg-red-500/10 px-3 py-2 text-red-300"
              : "text-xs rounded-md border border-cyan-500/30 bg-cyan-500/10 px-3 py-2 text-cyan-200"
          }
        >
          {notice.text}
        </p>
      )}

      {/* Branch info */}
      <div className="flex items-center gap-2 text-xs">
        <span className="font-mono rounded-md bg-white/[0.04] px-2 py-1 text-cyan-400">
          {pr.headBranch}
        </span>
        <svg width="12" height="12" viewBox="0 0 16 16" fill="none" className="text-gray-400 shrink-0">
          <path d="M4 8h8M9 5l3 3-3 3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span className="font-mono rounded-md bg-white/[0.04] px-2 py-1 text-gray-400">
          {pr.baseBranch}
        </span>
      </div>

      {/* Stats row */}
      <div className="flex items-center gap-4 text-xs">
        <span className="text-emerald-400 font-mono">+{pr.additions}</span>
        <span className="text-red-400 font-mono">-{pr.deletions}</span>
        <span className="text-gray-400">
          {t("filesChanged", { count: pr.changedFiles })}
        </span>

        {pr.mergeable !== null && (
          <Badge variant={pr.mergeable ? "success" : "warning"} size="sm">
            {pr.mergeable ? t("mergeable") : t("conflicts")}
          </Badge>
        )}

        <span className="font-mono text-gray-400 ml-auto" title={pr.headSha}>
          {pr.headSha.slice(0, 7)}
        </span>
      </div>

      {/* Merge action */}
      {canMerge && isApproved && (
        <RequirePermission permission="CodeReviewMerge">
        <div className="pt-1">
          {showMergeConfirm ? (
            <div className="flex items-center gap-2">
              <Button
                variant="success"
                size="sm"
                loading={isPending}
                disabled={isPending}
                onClick={handleMerge}
              >
                {repoIsRemote ? tForge("confirmPublish") : t("confirmMerge")}
              </Button>
              <Button
                variant="ghost"
                size="sm"
                onClick={() => setShowMergeConfirm(false)}
              >
                {tc("actions.cancel")}
              </Button>
            </div>
          ) : (
            <Button
              variant="success"
              size="sm"
              onClick={() => setShowMergeConfirm(true)}
            >
              <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <circle cx="5" cy="3.5" r="1.5" />
                <circle cx="5" cy="12.5" r="1.5" />
                <circle cx="11" cy="12.5" r="1.5" />
                <path d="M5 5v6M11 11V6.5a2 2 0 00-2-2H7" strokeLinecap="round" />
              </svg>
              {repoIsRemote ? tForge("publish") : t("mergePullRequest")}
            </Button>
          )}
          {repoIsRemote && <p className="mt-1.5 text-xs text-gray-400">{tForge("publishHint")}</p>}
        </div>
        </RequirePermission>
      )}
    </div>
  );
}

export { PrHeader };
