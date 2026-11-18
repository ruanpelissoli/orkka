import { useCallback, useState } from "react";
import { useTranslations } from "use-intl";
import { Button, Input } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { usePermissions } from "@/hooks/use-permissions";
import { useClientApi } from "@/hooks/use-client-api";
import { useBoardSignalR, type AutopilotChangedEvent } from "@/hooks/use-board-signalr";
import { api, ApiError } from "@/lib/api";
import { cn } from "@/lib/utils";
import type { AutopilotStatus, AutopilotStopReason } from "@/lib/types";

interface AutopilotToggleProps {
  /** Route ref used in API calls — slug or legacy ULID, resolved server-side. */
  projectId: string;
  /** Project ULID — SignalR board groups are keyed by it. */
  projectUlid: string;
  initialStatus: AutopilotStatus;
  /** Project Autonomy switch is off: scheduled autopilot windows won't fire. Manual start still works. */
  automationPaused?: boolean;
}

/**
 * How a finished session is described. The reason matters more than the fact: "finished" and
 * "ran out of money" and "three failures in a row" are very different mornings, and a single
 * "stopped" label would make the user open the logs to tell them apart.
 */
const STOP_LABEL_KEY: Record<AutopilotStopReason, string> = {
  Manual: "autopilotToggle.stopReasons.manual",
  Drained: "autopilotToggle.stopReasons.drained",
  CapReached: "autopilotToggle.stopReasons.capReached",
  BudgetBlocked: "autopilotToggle.stopReasons.budgetBlocked",
  ConsecutiveFailures: "autopilotToggle.stopReasons.consecutiveFailures",
  ProjectUnavailable: "autopilotToggle.stopReasons.projectUnavailable",
};

function formatUsd(value: number): string {
  return `$${value.toFixed(2)}`;
}

export function AutopilotToggle({ projectId, projectUlid, initialStatus, automationPaused }: AutopilotToggleProps) {
  const t = useTranslations("dashboard");
  const [status, setStatus] = useState(initialStatus);
  const [open, setOpen] = useState(false);
  const [capInput, setCapInput] = useState(
    initialStatus.capUsd != null ? String(initialStatus.capUsd) : "20"
  );
  const [uncapped, setUncapped] = useState(false);
  // Off every time the card opens, never remembered from the last session: it lets agents
  // decide questions a person would otherwise be asked, so it has to be a choice made for this
  // run. A sticky default is how it outlives the night it was meant for.
  const [answerEveryQuestion, setAnswerEveryQuestion] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const callApi = useClientApi();
  // Running with no ceiling is a billing decision, gated the same way server-side. Hiding the
  // option is convenience only — the endpoint is the boundary.
  const { has } = usePermissions();
  const canRunUncapped = has("BillingManage");
  // Answering every question covers deleting data, billing and production, so it keeps the
  // ProjectsManage bar it had as a project setting. The endpoint enforces it too.
  const canAnswerEveryQuestion = has("ProjectsManage");

  // Most of these arrive from the Worker, not from a click: a session that drains or caps out
  // overnight has to flip this toggle for whoever is watching the board.
  const handleAutopilotChanged = useCallback((event: AutopilotChangedEvent) => {
    setStatus(event.status);
  }, []);

  useBoardSignalR({ projectId: projectUlid, onAutopilotChanged: handleAutopilotChanged });

  const start = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      const parsed = Number(capInput);
      const next = await callApi((token) =>
        api.autopilot.start(
          projectId,
          {
            ...(uncapped ? { uncapped: true } : { capUsd: parsed }),
            answerEveryQuestion: canAnswerEveryQuestion && answerEveryQuestion,
          },
          token
        )
      );
      setStatus(next);
      setOpen(false);
      setAnswerEveryQuestion(false);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t("autopilotToggle.startError"));
    } finally {
      setBusy(false);
    }
  }, [answerEveryQuestion, callApi, canAnswerEveryQuestion, capInput, projectId, uncapped, t]);

  const stop = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await callApi((token) => api.autopilot.stop(projectId, token)));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t("autopilotToggle.stopError"));
    } finally {
      setBusy(false);
    }
  }, [callApi, projectId, t]);

  // A cap of 0 or NaN is rejected server-side too; disabling the button just avoids a round
  // trip to be told so.
  const capIsValid = uncapped || (Number(capInput) > 0 && Number.isFinite(Number(capInput)));

  return (
    <div className="relative">
      {automationPaused && (
        <span
          className="absolute -top-2 -right-2 z-10 rounded-full border border-amber-500/40 bg-amber-500/15 px-1.5 text-label text-amber-300"
          title={t("autopilotToggle.pausedTitle")}
          data-testid="autopilot-automation-paused"
        >
          ⏸
        </span>
      )}
      {/* Reads as a control, not a status pill. The previous styling was badge-shaped — tag
          padding, a near-invisible border, a leading dot, and hover that only changed the text
          colour — so it sat next to "7 tasks" looking like more of the same and people did not
          know it could be pressed. What fixes that is mostly the chevron (nothing else on the
          board opens a panel without one) plus a resting surface solid enough to look raised. */}
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-haspopup="dialog"
        aria-label={t("autopilotToggle.name")}
        className={cn(
          "group flex items-center gap-2 rounded-lg border px-3 py-1.5 text-xs font-medium",
          "transition-all active:scale-[0.98]",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary-500/50",
          status.running
            ? "border-primary-500/40 bg-primary-500/15 text-primary-100 hover:border-primary-500/60 hover:bg-primary-500/25"
            : cn(
                "border-white/[0.10] bg-white/[0.06] text-gray-200",
                "hover:border-white/[0.18] hover:bg-white/[0.12] hover:text-white",
                open && "border-white/[0.18] bg-white/[0.12] text-white"
              )
        )}
      >
        <span
          className={cn(
            "size-1.5 shrink-0 rounded-full",
            status.running ? "animate-pulse bg-primary-400" : "bg-gray-500"
          )}
        />
        {t("autopilotToggle.name")}
        {status.running && (
          <span className="text-xs text-primary-300/90">
            {status.capUsd != null
              ? `${formatUsd(status.spentUsd)} / ${formatUsd(status.capUsd)}`
              : formatUsd(status.spentUsd)}
          </span>
        )}
        <svg
          className={cn(
            "size-3 shrink-0 opacity-60 transition-transform group-hover:opacity-100",
            open && "rotate-180"
          )}
          viewBox="0 0 12 12"
          fill="none"
          aria-hidden="true"
        >
          <path
            d="M3 4.5 6 7.5 9 4.5"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </button>

      {open && (
        <div className="absolute right-0 z-30 mt-2 w-80 rounded-xl border border-white/[0.08] bg-surface-overlay p-4 shadow-xl">
          {status.running ? (
            <>
              <p className="text-sm font-medium text-white">{t("autopilotToggle.running")}</p>
              <dl className="mt-3 space-y-1.5 text-xs text-gray-400">
                <div className="flex justify-between">
                  <dt>{t("autopilotToggle.shipped")}</dt>
                  <dd className="text-gray-200">{status.tasksCompleted}</dd>
                </div>
                <div className="flex justify-between">
                  <dt>{t("autopilotToggle.inFlight")}</dt>
                  <dd className="text-gray-200">{status.tasksInFlight}</dd>
                </div>
                {status.tasksWaitingOnYou > 0 && (
                  <div className="flex justify-between">
                    <dt className="text-amber-300/80">{t("autopilotToggle.waitingOnYou")}</dt>
                    <dd className="text-amber-300">{status.tasksWaitingOnYou}</dd>
                  </div>
                )}
                <div className="flex justify-between">
                  <dt>{t("autopilotToggle.leftToPickUp")}</dt>
                  <dd className="text-gray-200">{status.draftingRemaining}</dd>
                </div>
                <div className="flex justify-between">
                  <dt>{t("autopilotToggle.spent")}</dt>
                  <dd className="text-gray-200">
                    {status.capUsd != null
                      ? t("autopilotToggle.spentOfCap", {
                          spent: formatUsd(status.spentUsd),
                          cap: formatUsd(status.capUsd),
                        })
                      : t("autopilotToggle.spentNoCap", { spent: formatUsd(status.spentUsd) })}
                  </dd>
                </div>
              </dl>

              {/* Said while it runs, not just when it starts: whoever opens this card mid-session
                  needs to know questions are being decided without them. */}
              {status.answerEveryQuestion && (
                <p
                  className="mt-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-2.5 py-2 text-xs text-amber-300"
                  data-testid="autopilot-answering-every-question"
                >
                  {t("autopilotToggle.answeringEveryQuestion")}
                </p>
              )}

              {/* Zero to pick up with work in flight is normal mid-wave — client tasks blocked
                  on the API tasks building their endpoints. Saying so stops it reading as stuck. */}
              {status.draftingRemaining === 0 && status.tasksInFlight > 0 && (
                <p className="mt-3 text-xs leading-relaxed text-gray-400">
                  {t("autopilotToggle.nothingNewToStart", { count: status.tasksInFlight })}
                </p>
              )}

              <RequirePermission permission="TasksExecute">
                <Button
                  size="sm"
                  variant="secondary"
                  className="mt-4 w-full"
                  onClick={stop}
                  disabled={busy}
                >
                  {busy ? t("autopilotToggle.stopping") : t("autopilotToggle.stop")}
                </Button>
                <p className="mt-2 text-xs leading-relaxed text-gray-400">
                  {t("autopilotToggle.stopNote")}
                </p>
              </RequirePermission>
            </>
          ) : (
            <>
              <p className="text-sm font-medium text-white">{t("autopilotToggle.idleTitle")}</p>
              <p className="mt-1 text-xs leading-relaxed text-gray-400">
                {t("autopilotToggle.idleDescription")}
              </p>

              {status.stopReason && (
                <p className="mt-3 rounded-lg border border-white/[0.06] bg-white/[0.02] px-2.5 py-2 text-xs text-gray-400">
                  {t("autopilotToggle.lastRun", {
                    reason: t(STOP_LABEL_KEY[status.stopReason]),
                    shipped: status.tasksCompleted,
                  })}
                  {status.tasksWaitingOnYou > 0 &&
                    `, ${t("autopilotToggle.lastRunWaiting", { count: status.tasksWaitingOnYou })}`}
                </p>
              )}

              <label className="mt-4 block text-xs font-medium text-gray-400">
                {t("autopilotToggle.capLabel")}
              </label>
              <Input
                type="number"
                min="1"
                step="1"
                value={capInput}
                disabled={uncapped}
                onChange={(e) => setCapInput(e.target.value)}
                className="mt-1"
              />
              <p className="mt-1 text-xs text-gray-400">
                {t("autopilotToggle.capHint")}
              </p>

              {canRunUncapped && (
                <label className="mt-3 flex items-center gap-2 text-xs text-gray-400">
                  <input
                    type="checkbox"
                    checked={uncapped}
                    onChange={(e) => setUncapped(e.target.checked)}
                  />
                  {t("autopilotToggle.uncapped")}
                </label>
              )}

              {canAnswerEveryQuestion && (
                <label className="mt-3 flex items-start gap-2 text-xs text-gray-400">
                  <input
                    type="checkbox"
                    className="mt-0.5"
                    checked={answerEveryQuestion}
                    onChange={(e) => setAnswerEveryQuestion(e.target.checked)}
                    data-testid="autopilot-answer-every-question"
                  />
                  <span>
                    <span className="text-gray-200">{t("autopilotToggle.answerEveryQuestion")}</span>
                    <span className="mt-0.5 block leading-relaxed">
                      {t("autopilotToggle.answerEveryQuestionHint")}
                    </span>
                  </span>
                </label>
              )}

              {error && <p className="mt-3 text-xs text-red-400">{error}</p>}

              <RequirePermission permission="TasksExecute">
                <Button
                  size="sm"
                  className="mt-4 w-full"
                  onClick={start}
                  disabled={busy || !capIsValid}
                >
                  {busy ? t("autopilotToggle.starting") : t("autopilotToggle.start")}
                </Button>
              </RequirePermission>
            </>
          )}
        </div>
      )}
    </div>
  );
}
