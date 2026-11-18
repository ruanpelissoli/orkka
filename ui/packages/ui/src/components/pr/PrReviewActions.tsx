import { useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { submitPrReview } from "@/lib/actions";
import type { PullRequestDetail } from "@/lib/types";
import type { TaskStatusKey } from "@/lib/constants";

interface PrReviewActionsProps {
  pr: PullRequestDetail;
  taskId: string;
  projectId: string;
  /**
   * Current task status, when the caller knows it. Approve/request-changes render only in
   * WaitingCodeReview — the one state where the pipeline is waiting on a human. While the
   * reviewer agent is mid-run the buttons are replaced with a note, because a manual approve
   * there races the agent's verdict (that race reopened a merged task in production).
   */
  taskStatus?: TaskStatusKey;
  onReviewSubmitted?: () => void;
}

/**
 * The decisions the API accepts, verbatim.
 *
 * These are validated server-side against exactly these three strings, and the component used to
 * send "Approved" / "ChangesRequested" — which every request failed on, before the handler ran.
 * Nobody noticed because the failure was swallowed here and masked by Next as an opaque server
 * action digest, and because the human Approve button is rarely reached in the autonomous
 * pipeline (Sentinel reviews there). Interactive sessions put a human in front of it routinely,
 * which is how it finally surfaced.
 */
const DECISION_APPROVE = "approve";
const DECISION_REQUEST_CHANGES = "request_changes";

function PrReviewActions({ pr, taskId, projectId, taskStatus, onReviewSubmitted }: PrReviewActionsProps) {
  const t = useTranslations("dashboard.prReview");
  const tc = useTranslations("common");
  const [isPending, startTransition] = useTransition();
  const [mode, setMode] = useState<"idle" | "requesting_changes">("idle");
  const [body, setBody] = useState("");
  const [error, setError] = useState<string | null>(null);

  if (pr.state !== "open") return null;

  // The reviewer agent still owns this review. Say so instead of offering buttons that would
  // race it — the controls appear the moment the task genuinely needs a human.
  if (taskStatus !== undefined && taskStatus !== "WaitingCodeReview") {
    return (
      <p className="text-xs text-gray-400">{t("agentStillReviewing")}</p>
    );
  }

  function handleSubmit(decision: string) {
    setError(null);
    startTransition(async () => {
      // Surfaced, not swallowed. "Handled by revalidation" was only ever true for success — a
      // rejected review left the button looking like it had worked.
      const result = await submitPrReview(
        taskId,
        decision,
        body.trim() || undefined,
        undefined,
        projectId
      );

      if (result?.error) {
        setError(result.error);
        return;
      }

      setBody("");
      setMode("idle");
      onReviewSubmitted?.();
    });
  }

  if (mode === "requesting_changes") {
    return (
      <RequirePermission permission="CodeReviewReview">
      <div className="space-y-3 pt-4 border-t border-white/[0.04]">
        <h4 className="text-label">
          {t("requestChanges")}
        </h4>
        <textarea
          value={body}
          onChange={(e) => setBody(e.target.value)}
          placeholder={t("requestChangesPlaceholder")}
          className="w-full rounded-xl bg-white/[0.03] border border-white/[0.06] px-4 py-2.5 text-sm text-gray-100 placeholder:text-gray-500 focus:outline-none focus:border-red-500/50 resize-none"
          rows={3}
          autoFocus
        />
        <div className="flex gap-2">
          <Button
            variant="danger"
            size="sm"
            disabled={isPending}
            loading={isPending}
            onClick={() => handleSubmit(DECISION_REQUEST_CHANGES)}
          >
            {t("submitReview")}
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setMode("idle");
              setBody("");
            }}
          >
            {tc("actions.cancel")}
          </Button>
        </div>
        {error && (
          <p className="text-xs text-red-400" role="alert">
            {error}
          </p>
        )}
      </div>
      </RequirePermission>
    );
  }

  return (
    <RequirePermission permission="CodeReviewReview">
    <div className="pt-4 border-t border-white/[0.04]">
    <div className="flex gap-2">
      <Button
        variant="success"
        size="md"
        className="flex-1"
        disabled={isPending}
        loading={isPending}
        onClick={() => handleSubmit(DECISION_APPROVE)}
      >
        <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
          <path d="M3 8.5l3.5 3.5L13 5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        {t("approve")}
      </Button>
      <Button
        variant="ghost"
        size="md"
        onClick={() => setMode("requesting_changes")}
      >
        {t("requestChanges")}
      </Button>
    </div>
    {error && (
      <p className="mt-3 text-xs text-red-400" role="alert">
        {error}
      </p>
    )}
    </div>
    </RequirePermission>
  );
}

export { PrReviewActions };
