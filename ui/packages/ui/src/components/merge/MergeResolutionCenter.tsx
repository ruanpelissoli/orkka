import { useState, useEffect, useCallback, useRef } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { GlassCard, Button, Skeleton } from "@/components/ui";
import { api, extractErrorMessage } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { useTaskSignalR } from "@/hooks/use-task-signalr";
import type {
  MergeConflictsInfo,
  MergeStrategy,
  ResolveMergeBody,
} from "@/lib/types";

/** What the running resolution is actually doing, named for the person watching. */
const RESOLVING_STRATEGY_KEY: Record<string, string> = {
  combine: "mergePage.resolvingStrategyCombine",
  task: "mergePage.resolvingStrategyTask",
  current: "mergePage.resolvingStrategyCurrent",
  perFile: "mergePage.resolvingStrategyPerFile",
};

/** "45s", "2m 05s" — digits and unit letters, so it reads the same in every locale. */
function formatElapsed(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const minutes = Math.floor(total / 60);
  const seconds = total % 60;
  return minutes === 0 ? `${seconds}s` : `${minutes}m ${String(seconds).padStart(2, "0")}s`;
}

interface MergeResolutionCenterProps {
  /** Task ULID (API identifier, not the per-project number). */
  taskId: string;
  projectId: string;
  taskNumber: number;
}

/**
 * Merge resolution page: explains what collided in product terms and offers three
 * choices (AI combine / keep task version / keep current app), plus an advanced
 * per-file mode. Polls while a resolution runs; the worker does all the git work.
 */
function MergeResolutionCenter({ taskId, projectId, taskNumber }: MergeResolutionCenterProps) {
  const t = useTranslations("dashboard");
  const withToken = useClientApi();

  const [info, setInfo] = useState<MergeConflictsInfo | null>(null);
  const [lastFetchFailed, setLastFetchFailed] = useState(false);
  // A failed poll after a successful load keeps showing what was loaded.
  const loadError = lastFetchFailed && !info;
  const [submitting, setSubmitting] = useState(false);
  // The server's own words, not a boolean. Every message this endpoint returns says
  // something the person needs ("a resolution is already in progress", "already merged"),
  // and collapsing them all into one generic line is what made the page look broken.
  const [submitError, setSubmitError] = useState<string | null>(null);
  // Live line from the run itself, so a long resolution never looks frozen.
  const [progress, setProgress] = useState<string | null>(null);
  const [tick, setTick] = useState(() => Date.now());
  const [cancelling, setCancelling] = useState(false);
  const [guidance, setGuidance] = useState("");
  const [confirming, setConfirming] = useState<"task" | "current" | null>(null);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [fileChoices, setFileChoices] = useState<Record<string, "task" | "current" | "ai">>({});
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const fetchInfo = useCallback(
    () =>
      withToken((token) => api.tasks.getMergeConflicts(taskId, token))
        .catch(() => null)
        .then((data) => {
          if (data) setInfo(data);
          // Only shown while there is nothing loaded yet — see `loadError` above.
          setLastFetchFailed(!data);
          return data;
        }),
    [taskId, withToken]
  );

  useEffect(() => {
    fetchInfo();
  }, [fetchInfo]);

  // Poll in every state something else can still change. It used to poll only while
  // resolving or blocked, which left the chooser stale in the one case that matters most: a
  // merge click arms the automatic resolver server-side (MergeAutoResolution), so the page
  // could be offering buttons for a resolution that was ALREADY running — and every click on
  // them came back "a resolution is already in progress" for a resolution the page had never
  // mentioned. Excluded: merged and nothing-to-do, plus exhausted, where automation has
  // stopped for good and nothing moves until the user picks something.
  useEffect(() => {
    if (info?.status === "none" || info?.status === "resolved" || info?.status === "exhausted")
      return;
    pollRef.current = setInterval(fetchInfo, 5000);
    return () => {
      if (pollRef.current) clearInterval(pollRef.current);
    };
  }, [info?.status, fetchInfo]);

  // The run's own progress, straight off the task hub. The resolver agent streams while it
  // works; without this the page has a spinner and nothing else to say for minutes at a time.
  useTaskSignalR({
    taskId,
    onStreamEvent: (event) => {
      if (event.type === "Progress" || event.type === "AgentThinking" || event.type === "ToolUse") {
        const line = event.content?.trim();
        if (line) setProgress(line.length > 200 ? line.slice(0, 200) + "\u2026" : line);
      }
    },
    // A merge failure ends the run — refetch rather than spin on into a state that is over.
    onTaskMergeFailed: () => {
      fetchInfo();
    },
  });

  // Leaving "resolving" clears the last progress line (adjusted during render on a status change).
  const status = info?.status;
  const [prevStatus, setPrevStatus] = useState(status);
  if (status !== prevStatus) {
    setPrevStatus(status);
    if (status !== "resolving") setProgress(null);
  }

  // The clock reads from the server's own start stamp, so it survives a reload and a second
  // browser sees the same number. Counting from when this page happened to open made a merge
  // that had been running for ten minutes look like it had just begun.
  const startedAtMs = info?.startedAt ? Date.parse(info.startedAt) : NaN;
  const elapsedMs = Number.isFinite(startedAtMs) ? tick - startedAtMs : null;

  useEffect(() => {
    if (info?.status !== "resolving" || !info?.startedAt) return;
    const id = setInterval(() => setTick(Date.now()), 1000);
    return () => clearInterval(id);
  }, [info?.status, info?.startedAt]);

  async function cancelQueued() {
    setCancelling(true);
    setSubmitError(null);
    try {
      await withToken((token) => api.tasks.cancelQueuedMerge(taskId, token));
    } catch (e) {
      setSubmitError(extractErrorMessage(e, t("mergePage.submitError")));
    } finally {
      setCancelling(false);
      await fetchInfo();
    }
  }

  async function submit(strategy: MergeStrategy) {
    setSubmitting(true);
    setSubmitError(null);
    try {
      const body: ResolveMergeBody = { strategy };
      if (strategy === "combine" && guidance.trim()) body.guidance = guidance.trim();
      if (strategy === "perFile") {
        body.files = (info?.files ?? []).map((f) => ({
          path: f.path,
          choice: fileChoices[f.path] ?? "ai",
        }));
      }
      await withToken((token) => api.tasks.resolveMerge(taskId, body, token));
      setConfirming(null);
      if (info?.status === "resolving") {
        // Steering an attempt already in flight: the server queued it, so re-read rather
        // than guess which of the two the page should now be describing.
        setGuidance("");
        await fetchInfo();
      } else {
        // Optimistic: show progress immediately; polling takes over.
        setInfo((prev) => (prev ? { ...prev, status: "resolving", pendingStrategy: strategy } : prev));
      }
    } catch (e) {
      // Most rejections here mean the work is already happening or already done — "a
      // resolution is already in progress", "this task is already merged". Those are not
      // failures from where the user is standing: they asked for the merge and the merge is
      // being handled. So re-read the truth and show THAT; only a state where the choice is
      // still theirs to make gets an error.
      const fresh = await fetchInfo();
      if (fresh?.status === "resolving" || fresh?.status === "resolved") {
        setConfirming(null);
      } else {
        setSubmitError(extractErrorMessage(e, t("mergePage.submitError")));
      }
    } finally {
      setSubmitting(false);
    }
  }

  if (loadError) {
    return (
      <GlassCard padding="lg">
        <div className="py-8 text-center">
          <p className="text-sm text-red-400">{t("mergePage.errorLoad")}</p>
          <Button variant="ghost" size="sm" className="mt-3" onClick={() => fetchInfo()}>
            {t("mergePage.retry")}
          </Button>
        </div>
      </GlassCard>
    );
  }

  if (!info) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-24" />
        <Skeleton className="h-40" />
      </div>
    );
  }

  if (info.status === "none" || info.status === "resolved") {
    const resolved = info.status === "resolved";
    return (
      <GlassCard padding="lg">
        <div className="py-8 text-center">
          <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-emerald-500/10 text-2xl">
            {resolved ? "✓" : "✨"}
          </div>
          <h2 className="mt-4 font-display text-lg font-semibold text-white">
            {t(resolved ? "mergePage.resolvedTitle" : "mergePage.noneTitle")}
          </h2>
          <p className="mt-2 text-sm text-gray-400">
            {t(resolved ? "mergePage.resolvedDesc" : "mergePage.noneDesc")}
          </p>
          <Link
            href={`/p/${projectId}/board`}
            className="mt-4 inline-block text-sm text-primary-400 hover:text-primary-300"
          >
            {t("mergePage.backToBoard")}
          </Link>
        </div>
      </GlassCard>
    );
  }

  if (info.status === "blocked") {
    // Environmental failure (e.g. uncommitted changes in the user's repo): no merge
    // strategy can help, so show the fix instead of the conflict chooser.
    return (
      <GlassCard padding="lg">
        <div className="py-8 text-center">
          <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-amber-500/10 text-2xl">
            🧹
          </div>
          <h2 className="mt-4 font-display text-lg font-semibold text-white">
            {t("mergePage.blockedTitle")}
          </h2>
          <p className="mx-auto mt-2 max-w-md text-sm text-gray-400">
            {t("mergePage.blockedDesc")}
          </p>
          {info.failureReason && (
            <p className="mx-auto mt-4 max-w-md rounded-xl border border-amber-500/20 bg-amber-500/[0.06] px-4 py-3 text-sm leading-relaxed text-amber-100">
              {info.failureReason}
            </p>
          )}
          <p className="mt-3 text-xs text-gray-400">{t("mergePage.blockedAutoRetry")}</p>
          <div className="mt-4">
            <Link
              href={`/p/${projectId}/tasks/${taskNumber}`}
              className="inline-block text-sm text-primary-400 hover:text-primary-300"
            >
              {t("mergePage.backToTask")}
            </Link>
          </div>
        </div>
      </GlassCard>
    );
  }

  if (info.status === "resolving") {
    const strategyKey =
      RESOLVING_STRATEGY_KEY[info.pendingStrategy ?? "combine"] ??
      RESOLVING_STRATEGY_KEY.combine;
    const queuedKey = info.queuedStrategy
      ? RESOLVING_STRATEGY_KEY[info.queuedStrategy] ?? RESOLVING_STRATEGY_KEY.combine
      : null;
    return (
      <GlassCard padding="lg">
        <div className="py-10 text-center">
          <div className="mx-auto h-8 w-8 animate-spin rounded-full border-2 border-cyan-500/30 border-t-cyan-400" />
          <h2 className="mt-5 font-display text-lg font-semibold text-white">
            {t("mergePage.resolvingTitle")}
          </h2>
          <p className="mx-auto mt-2 max-w-md text-sm text-gray-400">
            {t("mergePage.resolvingDesc")}
          </p>

          {/* What is running, how long it has been running, and the last thing it said. A
              bare spinner cannot distinguish "working" from "stuck", which is the whole
              complaint: three minutes of identical screen reads as a hung page. */}
          <div className="mx-auto mt-5 max-w-md space-y-2 rounded-xl border border-cyan-500/20 bg-cyan-500/[0.05] px-4 py-3 text-left">
            <div className="flex items-baseline justify-between gap-3">
              <span className="text-xs font-medium text-cyan-200">{t(strategyKey)}</span>
              {elapsedMs !== null && (
                <span className="shrink-0 font-mono text-xs text-gray-400">
                  {t("mergePage.resolvingElapsed", { time: formatElapsed(elapsedMs) })}
                </span>
              )}
            </div>
            <p className="break-words text-xs leading-relaxed text-gray-300">
              {progress ?? t("mergePage.resolvingStarting")}
            </p>
          </div>

          <p className="mx-auto mt-4 max-w-md text-xs text-gray-400">
            {t("mergePage.resolvingLeaveSafe")}
          </p>

          {/* Steering. Watching an attempt you already know is wrong, with no way to say so
              until it finishes, is the other half of the complaint — the answer is a queue of
              one that starts the moment this attempt ends without merging. The running
              attempt is never interrupted: killing it mid-write would leave the worktree
              half-resolved, and it may yet succeed. */}
          {queuedKey ? (
            <div className="mx-auto mt-5 max-w-md rounded-xl border border-white/[0.08] bg-white/[0.03] px-4 py-3 text-left">
              <p className="text-xs font-medium text-gray-200">
                {t("mergePage.steerQueuedLabel", { what: t(queuedKey) })}
              </p>
              {info.queuedGuidance && (
                <p className="mt-1.5 break-words text-xs italic leading-relaxed text-gray-400">
                  &ldquo;{info.queuedGuidance}&rdquo;
                </p>
              )}
              <Button
                variant="ghost"
                size="sm"
                className="mt-2"
                loading={cancelling}
                disabled={cancelling}
                onClick={cancelQueued}
              >
                {t("mergePage.steerCancel")}
              </Button>
            </div>
          ) : (
            <details className="group mx-auto mt-5 max-w-md text-left">
              <summary className="cursor-pointer select-none text-center text-xs text-gray-400 hover:text-gray-300">
                {t("mergePage.steerTitle")}
              </summary>
              <div className="mt-3 rounded-xl border border-white/[0.08] bg-white/[0.03] px-4 py-3">
                <p className="text-xs leading-relaxed text-gray-400">
                  {t("mergePage.steerDesc")}
                </p>
                <textarea
                  value={guidance}
                  onChange={(e) => setGuidance(e.target.value)}
                  placeholder={t("mergePage.guidancePlaceholder")}
                  rows={2}
                  maxLength={2000}
                  className="mt-3 w-full resize-none rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2 text-xs text-gray-100 placeholder:text-gray-500 focus:border-cyan-500/50 focus:outline-none"
                />
                <Button
                  variant="secondary"
                  size="sm"
                  className="mt-2 w-full"
                  loading={submitting}
                  disabled={submitting || !guidance.trim()}
                  onClick={() => submit("combine")}
                >
                  {t("mergePage.steerQueueButton")}
                </Button>
                <p className="mt-3 text-xs text-gray-400">{t("mergePage.steerInstead")}</p>
                <div className="mt-1.5 flex flex-wrap gap-2">
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={submitting}
                    onClick={() => submit("task")}
                  >
                    {t("mergePage.taskTitle")}
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={submitting}
                    onClick={() => submit("current")}
                  >
                    {t("mergePage.currentTitle")}
                  </Button>
                </div>
              </div>
            </details>
          )}

          {submitError && (
            <p className="mx-auto mt-3 max-w-md break-words text-xs text-red-400">
              {submitError}
            </p>
          )}

          <div className="mt-4">
            <Link
              href={`/p/${projectId}/tasks/${taskNumber}`}
              className="inline-block text-sm text-primary-400 hover:text-primary-300"
            >
              {t("mergePage.backToTask")}
            </Link>
          </div>
        </div>
      </GlassCard>
    );
  }

  // status === "awaitingChoice" | "exhausted" — the same choices either way; what differs is
  // what the page is allowed to claim about them.
  const exhausted = info.status === "exhausted";

  return (
    <div className="space-y-4">
      {/* Automation is out of moves. Said plainly and up front, because the alternative — the
          normal chooser with a cheerful "Recommended" badge on the option that has already
          failed three times — is what teaches people the button never works. Still a chooser
          and not a dead end: the deterministic side picks do not need the AI to succeed. */}
      {exhausted && (
        <GlassCard padding="lg" className="border-red-500/25 bg-red-500/[0.04]">
          <div className="flex gap-3">
            <div className="text-2xl leading-none">🛑</div>
            <div className="min-w-0">
              <h2 className="font-display text-lg font-semibold text-white">
                {t("mergePage.exhaustedTitle")}
              </h2>
              <p className="mt-1.5 text-sm leading-relaxed text-gray-300">
                {t("mergePage.exhaustedDesc")}
              </p>
              {info.failureReason && (
                <p className="mt-3 break-words rounded-xl border border-red-500/20 bg-red-500/[0.06] px-3 py-2 text-xs leading-relaxed text-red-100">
                  {info.failureReason}
                </p>
              )}
              <p className="mt-3 text-xs text-gray-400">{t("mergePage.exhaustedNext")}</p>
            </div>
          </div>
        </GlassCard>
      )}

      {/* What happened — reassurance + plain-language summary */}
      <GlassCard padding="lg">
        <h2 className="font-display text-lg font-semibold text-white">
          {t("mergePage.title")}
        </h2>
        {!exhausted && <p className="mt-1 text-sm text-gray-400">{t("mergePage.subtitle")}</p>}
        <p className="mt-3 rounded-xl border border-white/[0.06] bg-white/[0.02] px-4 py-3 text-sm leading-relaxed text-gray-200">
          {info.summary ??
            (info.files.length > 0
              ? t("mergePage.summaryFallback", { count: info.files.length })
              : t("mergePage.summaryFallbackNoCount"))}
        </p>

        {/* When the exhausted banner is up it has already shown the reason in full, so
            repeating it behind a "technical details" toggle just makes the same sentence
            look like two different findings. */}
        {((info.failureReason && !exhausted) || info.files.length > 0) && (
          <details className="group mt-3">
            <summary className="cursor-pointer select-none text-xs text-gray-400 hover:text-gray-300">
              {t("mergePage.technicalDetails")}
            </summary>
            <div className="mt-2 space-y-1.5 rounded-xl bg-black/20 px-3 py-2.5">
              {info.failureReason && !exhausted && (
                <p className="break-words text-xs text-amber-200/70">
                  {info.failureReason}
                </p>
              )}
              {info.files.map((f) => (
                <p key={f.path} className="break-words font-mono text-xs text-gray-400">
                  {f.path}
                  {f.detail ? ` — ${f.detail}` : ""}
                </p>
              ))}
            </div>
          </details>
        )}
      </GlassCard>

      {/* The three choices */}
      <div className="grid gap-4 md:grid-cols-3">
        <GlassCard
          padding="lg"
          className={`flex flex-col ${exhausted ? "border-white/[0.06]" : "border-cyan-500/20"}`}
        >
          <div className="text-2xl">✨</div>
          <h3 className="mt-2 font-display text-sm font-semibold text-white">
            {t("mergePage.combineTitle")}
            {exhausted ? (
              <span className="ml-2 rounded-full bg-white/[0.06] px-2 py-0.5 text-xs font-medium text-gray-400">
                {t("mergePage.alreadyTried")}
              </span>
            ) : (
              <span className="ml-2 rounded-full bg-cyan-500/15 px-2 py-0.5 text-xs font-medium text-cyan-300">
                {t("mergePage.recommended")}
              </span>
            )}
          </h3>
          <p className="mt-1.5 flex-1 text-xs leading-relaxed text-gray-400">
            {exhausted ? t("mergePage.combineRetryDesc") : t("mergePage.combineDesc")}
          </p>
          <textarea
            value={guidance}
            onChange={(e) => setGuidance(e.target.value)}
            placeholder={t("mergePage.guidancePlaceholder")}
            rows={2}
            maxLength={2000}
            className="mt-3 w-full resize-none rounded-xl border border-white/[0.06] bg-white/[0.03] px-3 py-2 text-xs text-gray-100 placeholder:text-gray-500 focus:border-cyan-500/50 focus:outline-none"
          />
          <Button
            variant={exhausted ? "secondary" : "primary"}
            size="sm"
            className="mt-3 w-full"
            loading={submitting}
            disabled={submitting}
            onClick={() => submit("combine")}
          >
            {exhausted ? t("mergePage.combineRetryButton") : t("mergePage.combineButton")}
          </Button>
        </GlassCard>

        <StrategyCard
          icon="📦"
          title={t("mergePage.taskTitle")}
          description={t("mergePage.taskDesc")}
          confirmLabel={t("mergePage.confirm")}
          cancelLabel={t("mergePage.cancel")}
          chooseLabel={t("mergePage.choose")}
          confirming={confirming === "task"}
          disabled={submitting}
          onSelect={() => setConfirming("task")}
          onCancel={() => setConfirming(null)}
          onConfirm={() => submit("task")}
        />

        <StrategyCard
          icon="🏠"
          title={t("mergePage.currentTitle")}
          description={t("mergePage.currentDesc")}
          confirmLabel={t("mergePage.confirm")}
          cancelLabel={t("mergePage.cancel")}
          chooseLabel={t("mergePage.choose")}
          confirming={confirming === "current"}
          disabled={submitting}
          onSelect={() => setConfirming("current")}
          onCancel={() => setConfirming(null)}
          onConfirm={() => submit("current")}
        />
      </div>

      {submitError && (
        <p className="mx-auto max-w-xl break-words text-center text-xs text-red-400">
          {submitError}
        </p>
      )}

      {/* Advanced: per-file resolution for technical users */}
      {info.files.length > 0 && (
        <GlassCard padding="lg">
          <button
            type="button"
            onClick={() => setAdvancedOpen((v) => !v)}
            className="flex w-full items-center justify-between text-left"
          >
            <span className="text-label">
              {t("mergePage.advancedTitle")}
            </span>
            <span className="text-gray-400">{advancedOpen ? "−" : "+"}</span>
          </button>
          {advancedOpen && (
            <div className="mt-3 space-y-2">
              <p className="text-xs text-gray-400">{t("mergePage.advancedHint")}</p>
              {info.files.map((f) => (
                <div
                  key={f.path}
                  className="flex flex-wrap items-center gap-2 rounded-xl bg-white/[0.02] px-3 py-2"
                >
                  <span className="min-w-0 flex-1 truncate font-mono text-xs text-gray-300">
                    {f.path}
                  </span>
                  <div className="flex gap-1">
                    {(["ai", "task", "current"] as const).map((choice) => {
                      const active = (fileChoices[f.path] ?? "ai") === choice;
                      return (
                        <button
                          key={choice}
                          type="button"
                          onClick={() =>
                            setFileChoices((prev) => ({ ...prev, [f.path]: choice }))
                          }
                          className={`rounded-lg px-2 py-1 text-xs transition-colors ${
                            active
                              ? "bg-cyan-500/20 text-cyan-300"
                              : "bg-white/[0.04] text-gray-400 hover:text-gray-300"
                          }`}
                        >
                          {t(
                            choice === "ai"
                              ? "mergePage.choiceAi"
                              : choice === "task"
                                ? "mergePage.choiceTask"
                                : "mergePage.choiceCurrent"
                          )}
                        </button>
                      );
                    })}
                  </div>
                </div>
              ))}
              <Button
                variant="secondary"
                size="sm"
                loading={submitting}
                disabled={submitting}
                onClick={() => submit("perFile")}
              >
                {t("mergePage.applyChoices")}
              </Button>
            </div>
          )}
        </GlassCard>
      )}

      <div className="text-center">
        <Link
          href={`/p/${projectId}/tasks/${taskNumber}`}
          className="text-xs text-gray-400 hover:text-gray-300"
        >
          {t("mergePage.backToTask")}
        </Link>
      </div>
    </div>
  );
}

function StrategyCard({
  icon,
  title,
  description,
  chooseLabel,
  confirmLabel,
  cancelLabel,
  confirming,
  disabled,
  onSelect,
  onCancel,
  onConfirm,
}: {
  icon: string;
  title: string;
  description: string;
  chooseLabel: string;
  confirmLabel: string;
  cancelLabel: string;
  confirming: boolean;
  disabled: boolean;
  onSelect: () => void;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <GlassCard padding="lg" className="flex flex-col">
      <div className="text-2xl">{icon}</div>
      <h3 className="mt-2 font-display text-sm font-semibold text-white">{title}</h3>
      <p className="mt-1.5 flex-1 text-xs leading-relaxed text-gray-400">{description}</p>
      {confirming ? (
        <div className="mt-3 flex gap-2">
          <Button
            variant="danger"
            size="sm"
            className="flex-1"
            disabled={disabled}
            onClick={onConfirm}
          >
            {confirmLabel}
          </Button>
          <Button variant="ghost" size="sm" disabled={disabled} onClick={onCancel}>
            {cancelLabel}
          </Button>
        </div>
      ) : (
        <Button
          variant="secondary"
          size="sm"
          className="mt-3 w-full"
          disabled={disabled}
          onClick={onSelect}
        >
          {chooseLabel}
        </Button>
      )}
    </GlassCard>
  );
}

export { MergeResolutionCenter, formatElapsed };
