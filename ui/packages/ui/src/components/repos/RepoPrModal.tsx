import { useState, useEffect, useCallback } from "react";
import { useTranslations } from "use-intl";
import { Modal, Badge, Button, Skeleton, Markdown } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { DiffFileCard } from "@/components/pr/DiffFileCard";
import { PrReviewHistory } from "@/components/pr/PrReviewHistory";
import { ForgePrLink } from "@/components/pr/ForgePrLink";
import { api, extractErrorMessage } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import type { PullRequestDetail } from "@/lib/types";

interface RepoPrModalProps {
  repoId: string;
  /** PR to show; null keeps the modal closed. */
  number: number | null;
  onClose: () => void;
  /** Called after a merge or review changes the PR (so lists can refresh). */
  onChanged?: () => void;
  /** A remote repo: landing the work publishes a forge PR (the runner's own CLI), it never
   * merges on the forge — so it is worded as publishing. */
  repoIsRemote?: boolean;
}

type ReviewDecision = "approve" | "request_changes" | "comment";

function RepoPrModal({ repoId, number, onClose, onChanged, repoIsRemote = false }: RepoPrModalProps) {
  const t = useTranslations("dashboard.repoMisc.prModal");
  const tForge = useTranslations("dashboard.forgePr");
  const withToken = useClientApi();

  // The loaded PR remembers which PR it is, so "loading" is derived: no
  // result for the current PR yet.
  const [loaded, setLoaded] = useState<{ key: string; pr: PullRequestDetail | null } | null>(null);
  const prKey = number !== null ? `${repoId}#${number}` : null;
  const current = loaded !== null && loaded.key === prKey ? loaded : null;
  const loading = prKey !== null && current === null;
  const pr = current?.pr ?? null;
  const error = current !== null && current.pr === null;

  const loadPr = useCallback(
    async (prNumber: number) => {
      const data = await withToken((token) =>
        api.repoPulls.get(repoId, prNumber, token)
      ).catch(() => null);
      return { key: `${repoId}#${prNumber}`, pr: data };
    },
    [repoId, withToken]
  );

  /** Re-reads the open PR after a merge or review (keeps it on screen meanwhile). */
  const fetchPr = useCallback(async () => {
    if (number === null) return;
    setLoaded(await loadPr(number));
  }, [number, loadPr]);

  useEffect(() => {
    if (number === null) return;
    let cancelled = false;
    loadPr(number).then((result) => {
      if (!cancelled) setLoaded(result);
    });
    return () => {
      cancelled = true;
    };
  }, [number, loadPr]);

  const merged = pr?.merged ?? pr?.state === "merged";
  const isOpen = pr?.state === "open" && !merged;

  return (
    <Modal
      open={number !== null}
      onClose={onClose}
      title={number !== null ? t("title", { number }) : t("titleBare")}
      className="md:max-w-4xl"
    >
      {loading ? (
        <div className="space-y-4">
          <Skeleton className="h-6 w-2/3" />
          <Skeleton className="h-4 w-1/3" />
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : error || !pr ? (
        <p className="py-6 text-center text-sm text-red-400">
          {t("loadFailed")}
        </p>
      ) : (
        <div className="space-y-4">
          {/* Title + state */}
          <div className="flex items-start gap-3 flex-wrap">
            <h3 className="font-display text-base font-semibold text-white flex-1 min-w-0">
              {pr.title}
              <span className="text-gray-400 font-normal ml-2">#{pr.number}</span>
            </h3>
            <div className="flex items-center gap-2 shrink-0">
              {pr.draft && <Badge variant="default" size="sm">{t("draft")}</Badge>}
              <Badge
                variant={merged ? "purple" : pr.state === "open" ? "success" : "error"}
                size="sm"
                dot
              >
                {merged
                  ? repoIsRemote
                    ? tForge("published")
                    : t("merged")
                  : pr.state === "open"
                    ? t("open")
                    : t("closed")}
              </Badge>
              {pr.forgeUrl && <ForgePrLink url={pr.forgeUrl} />}
            </div>
          </div>

          {/* Branches */}
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

          {/* Stats */}
          <div className="flex items-center gap-4 text-xs">
            <span className="text-emerald-400 font-mono">+{pr.additions}</span>
            <span className="text-red-400 font-mono">-{pr.deletions}</span>
            <span className="text-gray-400">
              {t("filesChanged", { count: pr.changedFiles })}
            </span>
            {isOpen && pr.mergeable !== null && (
              <Badge variant={pr.mergeable ? "success" : "warning"} size="sm">
                {pr.mergeable ? t("mergeable") : t("conflicts")}
              </Badge>
            )}
            <span className="font-mono text-gray-400 ml-auto" title={pr.headSha}>
              {pr.headSha.slice(0, 7)}
            </span>
          </div>

          {/* Body */}
          {pr.body && (
            <div className="rounded-xl border border-white/[0.06] bg-white/[0.02] px-4 py-3 text-sm text-gray-300">
              <Markdown>{pr.body}</Markdown>
            </div>
          )}

          {/* Merge / close actions. Closing is gated on the same permission as merging:
              both decide whether work lands, and rejecting someone's PR is not the lesser
              of the two. */}
          {isOpen && (
            <RequirePermission permission="CodeReviewMerge">
              <div className="flex flex-wrap items-start gap-2">
                <MergeButton
                  repoId={repoId}
                  number={pr.number}
                  publish={repoIsRemote}
                  onMerged={() => {
                    fetchPr();
                    onChanged?.();
                  }}
                />
                <ClosePrButton
                  repoId={repoId}
                  number={pr.number}
                  onClosed={() => {
                    fetchPr();
                    onChanged?.();
                  }}
                />
              </div>
            </RequirePermission>
          )}

          {/* Reviews history */}
          <PrReviewHistory reviews={pr.reviews} />

          {/* Files diff */}
          {pr.files.length > 0 && (
            <div className="space-y-3">
              <h4 className="text-label">
                {t("filesChangedHeading")}
              </h4>
              {pr.files.map((file) => (
                <DiffFileCard key={file.filename} file={file} readOnly />
              ))}
            </div>
          )}

          {/* Review form */}
          {isOpen && (
            <RequirePermission permission="CodeReviewReview">
              <ReviewForm
                repoId={repoId}
                number={pr.number}
                onSubmitted={() => {
                  fetchPr();
                  onChanged?.();
                }}
              />
            </RequirePermission>
          )}
        </div>
      )}
    </Modal>
  );
}

function MergeButton({
  repoId,
  number,
  onMerged,
  publish,
}: {
  repoId: string;
  number: number;
  onMerged: () => void;
  /** Remote repo: the action opens a forge PR rather than merging. */
  publish?: boolean;
}) {
  const t = useTranslations("dashboard.repoMisc.prModal");
  const tForge = useTranslations("dashboard.forgePr");
  const tActions = useTranslations("common.actions");
  const withToken = useClientApi();
  const [confirming, setConfirming] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleMerge() {
    setPending(true);
    setError(null);
    try {
      await withToken((token) => api.repoPulls.merge(repoId, number, token));
      onMerged();
    } catch (e) {
      // The API says what actually happened — e.g. "conflicts detected, Orkka is
      // combining the changes automatically" — so show that, not a guess.
      setError(extractErrorMessage(e, t("mergeFailed")));
    } finally {
      setPending(false);
      setConfirming(false);
    }
  }

  return (
    <div className="pt-1">
      {confirming ? (
        <div className="flex items-center gap-2">
          <Button
            variant="success"
            size="sm"
            loading={pending}
            disabled={pending}
            onClick={handleMerge}
          >
            {publish ? tForge("confirmPublish") : t("confirmMerge")}
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setConfirming(false)}>
            {tActions("cancel")}
          </Button>
        </div>
      ) : (
        <Button variant="success" size="sm" onClick={() => setConfirming(true)}>
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
            <circle cx="5" cy="3.5" r="1.5" />
            <circle cx="5" cy="12.5" r="1.5" />
            <circle cx="11" cy="12.5" r="1.5" />
            <path d="M5 5v6M11 11V6.5a2 2 0 00-2-2H7" strokeLinecap="round" />
          </svg>
          {publish ? tForge("publish") : t("mergePr")}
        </Button>
      )}
      {publish && !error && <p className="mt-1.5 text-xs text-gray-400">{tForge("publishHint")}</p>}
      {error && (
        <p
          className={`mt-2 text-xs ${
            /automatic/i.test(error) ? "text-emerald-400" : "text-red-400"
          }`}
        >
          {error}
        </p>
      )}
    </div>
  );
}

/// Closing is destructive-ish and easy to hit by accident next to Merge, so it confirms the
/// same way — and says what survives, since "close" reads as "discard" to most people.
function ClosePrButton({
  repoId,
  number,
  onClosed,
}: {
  repoId: string;
  number: number;
  onClosed: () => void;
}) {
  const t = useTranslations("dashboard.repoMisc.prModal");
  const tActions = useTranslations("common.actions");
  const withToken = useClientApi();
  const [confirming, setConfirming] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState(false);

  async function handleClose() {
    setPending(true);
    setError(false);
    try {
      await withToken((token) => api.repoPulls.close(repoId, number, token));
      onClosed();
    } catch {
      setError(true);
    } finally {
      setPending(false);
      setConfirming(false);
    }
  }

  return (
    <div className="pt-1">
      {confirming ? (
        <div className="flex items-center gap-2">
          <Button
            variant="danger"
            size="sm"
            loading={pending}
            disabled={pending}
            onClick={handleClose}
          >
            {t("closeWithoutMerging")}
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setConfirming(false)}>
            {tActions("cancel")}
          </Button>
        </div>
      ) : (
        <Button variant="ghost" size="sm" onClick={() => setConfirming(true)}>
          {t("closePr")}
        </Button>
      )}
      {confirming && !error && (
        <p className="mt-2 text-xs text-gray-400">
          {t("closeHint")}
        </p>
      )}
      {error && (
        <p className="mt-2 text-xs text-red-400">
          {t("closeFailed")}
        </p>
      )}
    </div>
  );
}

function ReviewForm({
  repoId,
  number,
  onSubmitted,
}: {
  repoId: string;
  number: number;
  onSubmitted: () => void;
}) {
  const t = useTranslations("dashboard.repoMisc.prModal");
  const withToken = useClientApi();
  const [body, setBody] = useState("");
  const [pending, setPending] = useState(false);
  const [error, setError] = useState(false);
  const [submitted, setSubmitted] = useState<ReviewDecision | null>(null);

  async function handleSubmit(decision: ReviewDecision) {
    // Approvals may go without a message; the other decisions need one.
    if (decision !== "approve" && !body.trim()) return;
    setPending(true);
    setError(false);
    setSubmitted(null);
    try {
      await withToken((token) =>
        api.repoPulls.submitReview(
          repoId,
          number,
          { decision, body: body.trim() || undefined },
          token
        )
      );
      setBody("");
      setSubmitted(decision);
      onSubmitted();
    } catch {
      setError(true);
    } finally {
      setPending(false);
    }
  }

  return (
    <div className="space-y-3 pt-4 border-t border-white/[0.04]">
      <h4 className="text-label">
        {t("submitReview")}
      </h4>
      <textarea
        value={body}
        onChange={(e) => setBody(e.target.value)}
        placeholder={t("reviewPlaceholder")}
        className="w-full rounded-xl bg-white/[0.03] border border-white/[0.06] px-4 py-2.5 text-sm text-gray-100 placeholder:text-gray-500 focus:outline-none focus:border-cyan-500/50 resize-none"
        rows={3}
      />
      <div className="flex flex-wrap gap-2">
        <Button
          variant="success"
          size="sm"
          disabled={pending}
          loading={pending}
          onClick={() => handleSubmit("approve")}
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M3 8.5l3.5 3.5L13 5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {t("approve")}
        </Button>
        <Button
          variant="danger"
          size="sm"
          disabled={pending || !body.trim()}
          onClick={() => handleSubmit("request_changes")}
        >
          {t("requestChanges")}
        </Button>
        <Button
          variant="ghost"
          size="sm"
          disabled={pending || !body.trim()}
          onClick={() => handleSubmit("comment")}
        >
          {t("comment")}
        </Button>
      </div>
      {error && (
        <p className="text-xs text-red-400">{t("reviewFailed")}</p>
      )}
      {submitted && !error && (
        <p className="flex items-center gap-1.5 text-xs text-emerald-400">
          <svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M3 8.5l3.5 3.5L13 5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {submitted === "approve"
            ? t("approved")
            : submitted === "request_changes"
              ? t("changesRequested")
              : t("commentPosted")}
        </p>
      )}
    </div>
  );
}

export { RepoPrModal };
