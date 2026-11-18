import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import type { MilestonePlanChangedEvent } from "@/hooks/use-board-signalr";
import type {
  MilestonePlanJob,
  MilestonePlanResponse,
  MilestonePlanStep,
} from "@/lib/types";

/**
 * Owns one milestone-planning run, from the click to the proposal.
 *
 * Planning takes 90s–210s. It used to happen inside the POST, so a user who got bored of a
 * spinner and navigated away tore down the fetch — and with it a generation their org had
 * already paid for. Now the request only queues the run; this hook follows it.
 *
 * Two things it must get right, both of them consequences of that:
 *
 * - **Reattach.** The run outlives this component, so mounting reads the server's answer for
 *   "is something already planning here" instead of assuming nothing is. A reload, a second
 *   tab, or a different device all land back on the run in progress.
 * - **Say what is happening.** `step` and `elapsedSeconds` exist because the honest reason the
 *   original request died is that nothing on screen changed for three and a half minutes.
 */

interface UseMilestonePlanOptions {
  /** Route ref used in URLs — the project slug (or a legacy ULID). */
  projectId: string;
  /** Planning INTO an existing milestone. Omitted plans a new one; the two are separate runs
   * and must not reattach to each other. */
  milestoneId?: string;
}

interface UseMilestonePlanResult {
  /** The proposal, once there is one. The user edits this before accepting. */
  proposal: MilestonePlanResponse | null;
  setProposal: (next: MilestonePlanResponse | null) => void;
  /** True from the click until the run finishes — including across a reload. */
  planning: boolean;
  /** The phase the run has reached, or null when nothing is running. */
  step: MilestonePlanStep | null;
  /** Whole seconds since the run was claimed. Ticks while planning. */
  elapsedSeconds: number;
  /** `runner_offline` means no runner was reachable, not that planning failed. */
  failureCode: MilestonePlanJob["failureCode"];
  error: string | null;
  /** Queues a run. Resolves as soon as it is queued, not when it finishes. */
  start: (goal?: string) => Promise<void>;
  /** Clears a finished or failed run from view (after accepting, or dismissing an error). */
  reset: () => void;
  /** Feed this the board group's `MilestonePlanChanged` events. */
  onPlanChanged: (event: MilestonePlanChangedEvent) => void;
}

export function useMilestonePlan({
  projectId,
  milestoneId,
}: UseMilestonePlanOptions): UseMilestonePlanResult {
  const withToken = useClientApi();

  const [job, setJob] = useState<MilestonePlanJob | null>(null);
  const [proposal, setProposal] = useState<MilestonePlanResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());

  // Read inside callbacks that must not re-subscribe every time the job moves — a SignalR
  // handler that changes identity on every step would tear down and rebuild its subscription
  // three times per run.
  const jobRef = useRef<MilestonePlanJob | null>(null);
  useEffect(() => {
    jobRef.current = job;
  }, [job]);

  const running = job?.status === "Pending" || job?.status === "Running";

  const applyJob = useCallback((next: MilestonePlanJob | null) => {
    // Kept in step with the state so a handler firing before the effect below still sees the
    // job it is being told about.
    jobRef.current = next;
    setJob(next);
    if (next?.plan) setProposal(next.plan);
    setError(next?.status === "Failed" ? (next.error ?? "") : null);
  }, []);

  // Reattach on mount. Without this, a reload during a run shows a Plan button as though
  // nothing were happening — and clicking it would look like the only way forward.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const active = await withToken((token) =>
          api.milestones.activePlanJob(projectId, milestoneId, token)
        );
        if (!cancelled && active) applyJob(active);
      } catch {
        // Nothing to reattach to is the normal case; a failure here must not block the page.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [projectId, milestoneId, withToken, applyJob]);

  // A clock that only runs while something is planning. The elapsed figure itself is derived
  // below rather than stored, so there is no second copy of the truth to fall out of step.
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);

  // Counted from when a worker CLAIMED the run, not from the click: the queue wait is not the
  // user's model call, and counting it would overstate every run. Clamped at zero because
  // `now` is only refreshed by the tick above, so it can briefly predate a run that just
  // started — one second later it is right.
  const startedAtMs = job?.startedAt ? Date.parse(job.startedAt) : null;
  const elapsedSeconds =
    running && startedAtMs !== null
      ? Math.max(0, Math.floor((now - startedAtMs) / 1000))
      : 0;

  /**
   * A run finished — fetch it, because the event carries no plan.
   *
   * Deliberately by job id rather than "the active one": by the time this resolves the user may
   * have started another run, and overwriting the new proposal with the old one would be worse
   * than showing nothing.
   */
  const refresh = useCallback(
    async (jobId: string) => {
      try {
        const fresh = await withToken((token) =>
          api.milestones.planJob(projectId, jobId, token)
        );
        if (jobRef.current?.jobId === jobId) applyJob(fresh);
      } catch {
        // The socket already told us the outcome; a failed re-read only costs the plan body,
        // and the next mount reattaches to it.
      }
    },
    [projectId, withToken, applyJob]
  );

  const onPlanChanged = useCallback(
    (event: MilestonePlanChangedEvent) => {
      // The board group carries every member's runs. Only ours, and only this milestone's.
      const current = jobRef.current;
      if (!current || event.jobId !== current.jobId) return;

      const next: MilestonePlanJob = {
        ...current,
        status: event.status,
        step: event.step,
        failureCode: event.failureCode,
        error: event.errorMessage,
        startedAt: event.startedAt ?? current.startedAt,
      };
      jobRef.current = next;
      setJob(next);

      if (event.status === "Failed") setError(event.errorMessage ?? "");
      if (event.status === "Completed") void refresh(event.jobId);
    },
    [refresh]
  );

  const start = useCallback(
    async (goal?: string) => {
      setError(null);
      setProposal(null);
      try {
        const queued = await withToken((token) =>
          api.milestones.plan(
            projectId,
            { goal: goal?.trim() || undefined, milestoneId },
            token
          )
        );
        applyJob(queued);

        // A run that finished between queueing and this line — a cached plan the server handed
        // straight back, or a very fast one — has no further events coming.
        if (queued.status === "Completed") await refresh(queued.jobId);
      } catch (e) {
        // Queueing is a fast call now: a failure here is a real rejection (no permission, no
        // such repo, rate limited), not a user who ran out of patience.
        setError(e instanceof ApiError ? e.body : "");
        setJob(null);
      }
    },
    [projectId, milestoneId, withToken, applyJob, refresh]
  );

  const reset = useCallback(() => {
    setJob(null);
    setProposal(null);
    setError(null);
  }, []);

  return {
    proposal,
    setProposal,
    planning: running,
    step: running ? (job?.step ?? "Queued") : null,
    elapsedSeconds,
    failureCode: job?.failureCode ?? null,
    error,
    start,
    reset,
    onPlanChanged,
  };
}
