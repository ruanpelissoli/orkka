import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { parseCommitDiff } from "@/components/repos/commit-diff-parser";
import type { StreamEvent, WorkspaceChangedEvent } from "@/hooks/use-task-signalr";
import type { PrFile } from "@/lib/types";
import { parseToolEvent } from "./tool-event-parser";
import {
  emptyModel,
  withBaseline,
  applyToolEvent,
  totals as computeTotals,
  type WorkspaceModel,
  type WorkspaceBaseline,
} from "./workspace-model";

interface UseLiveWorkspaceOptions {
  taskId: string | null;
  /** Non-null when the task has a PR — the fallback baseline once no snapshot exists. */
  prUrl?: string | null;
  /** Re-fetching the baseline is keyed on this; the caller bumps it on lane changes. */
  baselineKey?: string | number;
}

type LoadState = "idle" | "loading" | "ready" | "error";

interface WorkspaceState {
  taskId: string | null;
  model: WorkspaceModel;
  load: LoadState;
}

function fresh(taskId: string | null): WorkspaceState {
  return { taskId, model: emptyModel(), load: taskId ? "loading" : "idle" };
}

/**
 * Owns the workspace model for the selected task:
 *  - loads the baseline (the run's live working-tree snapshot when there is one, else the
 *    newest stored code-change snapshot, else the PR diff),
 *  - ingests `ToolUse` stream events into live effects (Tier A: instant, approximate),
 *  - applies `WorkspaceChanged` pushes (Tier B: the worker re-diffed the worktree — exact),
 *  - re-baselines when asked (lane changed → a new snapshot may exist).
 *
 * State is tagged with the task it belongs to, so switching tasks reads as an empty model on
 * the very next render — no reset-in-effect, no flash of the previous task's files.
 */
function useLiveWorkspace({ taskId, prUrl, baselineKey }: UseLiveWorkspaceOptions) {
  const withToken = useClientApi();
  const [state, setState] = useState<WorkspaceState>(() => fresh(taskId));
  const loadSeq = useRef(0);
  // Highest live capture revision accepted for the current task. A ref, not state: the
  // decision to fetch a push's diff must be made synchronously in the SignalR callback, and a
  // React updater function is not guaranteed to run there (only when the queue is empty).
  const liveRevision = useRef<{ taskId: string | null; revision: number }>({ taskId: null, revision: 0 });

  const current = state.taskId === taskId ? state : fresh(taskId);

  const loadBaseline = useCallback(
    async (id: string) => {
      const seq = ++loadSeq.current;
      let files: PrFile[] = [];
      let baseline: WorkspaceBaseline = {
        source: "none",
        branchName: null,
        baseBranch: null,
        capturedAt: null,
        truncated: false,
        revision: 0,
      };
      try {
        // The API lists a live row (run in progress) first, then permanent snapshots newest-first.
        const snapshots = await withToken((token) => api.tasks.codeChanges(id, token));
        const newest = snapshots[0];
        if (newest) {
          const diff = await withToken((token) => api.tasks.codeChangeDiff(id, newest.id, token));
          files = parseCommitDiff(diff.diff);
          baseline = {
            source: diff.isLive ? "live" : "snapshot",
            branchName: diff.branchName,
            baseBranch: diff.baseBranch,
            capturedAt: diff.capturedAt,
            truncated: diff.isTruncated,
            revision: diff.isLive ? (diff.revision ?? 0) : 0,
            unavailable: !!diff.unavailable,
          };
        } else if (prUrl) {
          const pr = await withToken((token) => api.pullRequests.get(id, token));
          files = pr.files;
          baseline = {
            source: "pr",
            branchName: pr.headBranch,
            baseBranch: pr.baseBranch,
            capturedAt: null,
            truncated: false,
            revision: 0,
          };
        }
        if (seq !== loadSeq.current) return;
        if (baseline.source === "live") {
          const known = liveRevision.current.taskId === id ? liveRevision.current.revision : 0;
          if (baseline.revision > known) liveRevision.current = { taskId: id, revision: baseline.revision };
        }
        setState((prev) => {
          const base = prev.taskId === id ? prev : fresh(id);
          // A live push may have landed a newer capture while this load was in flight.
          if (baseline.source === "live" && base.model.baseline.source === "live" && base.model.baseline.revision > baseline.revision) {
            return { ...base, load: "ready" };
          }
          return { ...base, model: withBaseline(base.model, files, baseline), load: "ready" };
        });
      } catch {
        if (seq !== loadSeq.current) return;
        // A failed baseline is not a failed view — live effects still render. Surface it
        // so the toolbar can say "preview only".
        setState((prev) => {
          const base = prev.taskId === id ? prev : fresh(id);
          return { ...base, load: "error" };
        });
      }
    },
    [withToken, prUrl]
  );

  // Task switch or explicit re-key (lane moved): fetch the baseline. The model for a new task
  // is already empty by derivation above. Deferred to a microtask so no setState is reachable
  // from the effect body itself; loadSeq already guards staleness.
  useEffect(() => {
    if (!taskId) return;
    const id = taskId;
    let cancelled = false;
    queueMicrotask(() => {
      if (!cancelled) void loadBaseline(id);
    });
    return () => {
      cancelled = true;
    };
  }, [taskId, baselineKey, loadBaseline]);

  /** Re-fetch the baseline without dropping live effects (a snapshot may have landed). */
  const reload = useCallback(() => {
    if (!taskId) return;
    void loadBaseline(taskId);
  }, [taskId, loadBaseline]);

  /**
   * The worker captured the working tree (WorkspaceChanged). Two steps so the tree never
   * waits on a download: the event's file list becomes the baseline at once (stats, no
   * patches), then the diff is fetched and swapped in. Older/duplicate revisions are dropped;
   * live edits older than the capture are folded away since the capture now covers them.
   */
  const applyWorkspaceChanged = useCallback(
    (event: WorkspaceChangedEvent) => {
      if (!taskId || event.taskId !== taskId) return;
      const known = liveRevision.current.taskId === taskId ? liveRevision.current.revision : 0;
      if (event.revision <= known) return;
      liveRevision.current = { taskId, revision: event.revision };

      const header = {
        source: "live" as const,
        branchName: event.branchName,
        baseBranch: event.baseBranch,
        capturedAt: event.timestamp,
        truncated: event.isTruncated,
        revision: event.revision,
      };

      setState((prev) => {
        const base = prev.taskId === taskId ? prev : fresh(taskId);
        if (base.model.baseline.source === "live" && base.model.baseline.revision >= event.revision) return prev;
        const stubs: PrFile[] = event.files.map((f) => ({
          filename: f.path,
          status: f.status,
          additions: f.additions,
          deletions: f.deletions,
          patch: null,
          previousFilename: null,
        }));
        // Keep patches we already have for files whose stats did not change — no flash.
        const knownFiles = base.model.files;
        for (const stub of stubs) {
          const k = knownFiles[stub.filename];
          if (k && k.patch && k.additions === stub.additions && k.deletions === stub.deletions) stub.patch = k.patch;
        }
        return { ...base, model: withBaseline(base.model, stubs, header), load: "ready" };
      });

      void withToken((token) => api.tasks.codeChangeDiff(taskId, event.snapshotId, token))
        .then((diff) => {
          // Superseded while downloading (a newer push, or a task switch) → drop.
          if (liveRevision.current.taskId !== taskId || liveRevision.current.revision !== event.revision) return;
          const files = parseCommitDiff(diff.diff);
          setState((prev) => {
            const base = prev.taskId === taskId ? prev : fresh(taskId);
            if (base.model.baseline.source !== "live" || base.model.baseline.revision !== event.revision) return prev;
            return { ...base, model: withBaseline(base.model, files, { ...header, truncated: diff.isTruncated }), load: "ready" };
          });
        })
        .catch(() => {
          // The stubs already updated the tree; the next capture will bring patches.
        });
    },
    [taskId, withToken]
  );

  /** Feed a stream event; non-mutating events are ignored cheaply. */
  const ingest = useCallback(
    (event: StreamEvent) => {
      const parsed = parseToolEvent(event);
      if (!parsed || !taskId) return;
      setState((prev) => {
        const base = prev.taskId === taskId ? prev : fresh(taskId);
        const model = applyToolEvent(base.model, parsed);
        return model === base.model && base === prev ? prev : { ...base, model };
      });
    },
    [taskId]
  );

  const totals = useMemo(() => computeTotals(current.model), [current.model]);

  return { model: current.model, totals, loadState: current.load, ingest, applyWorkspaceChanged, reload };
}

export { useLiveWorkspace };
export type { LoadState };
