/**
 * Reusable task-pipeline flows (every run is on the developer's runner). REST polling is the
 * source of truth; hub events are recorded as evidence by the callers that want them.
 */
import { cfg } from "../../config/index.js";
import type { OrkkaApi } from "../api/client.js";
import { pollUntil } from "../api/poll.js";
import type { CreateTaskRequest, Lane, ProjectRepoResponse, ProjectResponse, TaskResponse, TaskStatus } from "../api/types.js";
import type { BareRemote } from "../testdata/bare-remote.js";
import { fixtureRepos, standardTask, type FixtureRepo } from "../testdata/fixture-repos.js";
import { featureToken } from "../testdata/naming.js";
import { log } from "../util/log.js";

export const TERMINAL_STATUSES: TaskStatus[] = ["Completed", "Archived"];
export const PARKED_STATUSES: TaskStatus[] = [
  "WaitingPlanApproval",
  "WaitingCodeReview",
  "WaitingAnswer",
  "WaitingQaReview",
  "WaitingUserVerification",
  "WaitingForRunner",
  "OutOfTokens",
  "ReasoningTimeout",
  "Blocked",
];

/**
 * Add a local bare remote to the project by its forge-looking alias URL
 * (https://github.com/e2e-local/<name>.git → GitHub, published with the stub gh). Only the
 * harness's sandboxed runner can reach it (its git rewrites the alias to the local folder).
 */
export async function linkRemote(api: OrkkaApi, project: ProjectResponse, remote: BareRemote, opts: { provider?: string; cliAccount?: string | null } = {}): Promise<ProjectRepoResponse> {
  const repo = await api.repos.add(project.id, { name: remote.name, gitUrl: remote.aliasUrl, defaultBranch: remote.defaultBranch, provider: opts.provider ?? null, cliAccount: opts.cliAccount ?? null });
  log.info(`Linked ${remote.aliasUrl} → repo ${repo.id} (${repo.provider}, ${repo.defaultBranch})`);
  return repo;
}

export interface StandardTaskResult {
  task: TaskResponse;
  token: string;
}

/** Create (and by default submit) the standard tiny task against a repo. */
export async function createStandardTask(api: OrkkaApi, project: ProjectResponse, repo: ProjectRepoResponse, opts: Partial<CreateTaskRequest> & { submit?: boolean; fixture?: FixtureRepo } = {}): Promise<StandardTaskResult> {
  const fixture = opts.fixture ?? fixtureRepos.web;
  const token = featureToken();
  const { title, description } = standardTask(fixture, token);
  const { submit = true, fixture: _f, ...rest } = opts;
  const task = await api.tasks.create(project.id, {
    repoId: repo.id,
    title,
    description,
    priority: "Medium",
    type: "Feature",
    skipTesting: true,
    ...rest,
  });
  log.info(`Created task #${task.number} ${task.code} runner=${task.runnerUserId ?? "-"} model=${task.model ?? "agent default"}`);
  if (submit) await api.tasks.submit(task.id);
  return { task: submit ? await api.tasks.get(task.id) : task, token };
}

export interface LaneTrace {
  at: string;
  lane: string;
  status: string;
}

/**
 * Follow a task until it reaches one of `until` lanes/statuses, a terminal status, or parks in a
 * human-action state. Records every lane/status transition it observes.
 */
export async function followTask(
  api: OrkkaApi,
  taskId: string,
  opts: { until?: { lanes?: Lane[]; statuses?: TaskStatus[] }; stopWhenParked?: boolean; deadlineMs?: number; onChange?: (t: TaskResponse) => void } = {},
): Promise<{ task: TaskResponse; trace: LaneTrace[] }> {
  const trace: LaneTrace[] = [];
  let last = "";
  const task = await pollUntil<TaskResponse>({
    label: `task ${taskId} to reach ${JSON.stringify(opts.until ?? "terminal")}`,
    deadlineMs: opts.deadlineMs ?? cfg.timeouts.aiPhaseMs,
    intervalMs: 8000,
    fetch: () => api.tasks.get(taskId),
    onTick: (t) => {
      const key = `${t.currentLane}/${t.status}`;
      if (key !== last) {
        last = key;
        trace.push({ at: new Date().toISOString(), lane: t.currentLane, status: t.status });
        log.info(`task #${t.number}: ${t.currentLane} / ${t.status}${t.queuePosition != null ? ` (queue ${t.queuePosition})` : ""}`);
        opts.onChange?.(t);
      }
    },
    done: (t) => {
      if (opts.until?.lanes?.includes(t.currentLane as Lane)) return true;
      if (opts.until?.statuses?.includes(t.status as TaskStatus)) return true;
      if (TERMINAL_STATUSES.includes(t.status as TaskStatus) || t.currentLane === "Done") return true;
      if ((opts.stopWhenParked ?? true) && PARKED_STATUSES.includes(t.status as TaskStatus)) return true;
      return false;
    },
  });
  return { task, trace };
}

/** Drive a task through the whole pipeline, answering the human loops when they appear. */
export async function driveToDone(
  api: OrkkaApi,
  taskId: string,
  opts: { approvePlans?: boolean; approveReviews?: boolean; mergeWhenApproved?: boolean; answerQuestions?: (task: TaskResponse) => Promise<string>; deadlineMs?: number; onChange?: (t: TaskResponse) => void } = {},
): Promise<{ task: TaskResponse; trace: LaneTrace[]; humanActions: string[] }> {
  const started = Date.now();
  const deadline = opts.deadlineMs ?? cfg.timeouts.aiPhaseMs;
  const trace: LaneTrace[] = [];
  const humanActions: string[] = [];
  for (;;) {
    const remaining = deadline - (Date.now() - started);
    if (remaining <= 0) throw new Error(`driveToDone: deadline exceeded for task ${taskId}; trace=${JSON.stringify(trace)}`);
    const r = await followTask(api, taskId, { deadlineMs: remaining, onChange: opts.onChange });
    trace.push(...r.trace);
    const t = r.task;
    if (t.currentLane === "Done" || TERMINAL_STATUSES.includes(t.status as TaskStatus)) return { task: t, trace, humanActions };
    switch (t.status) {
      case "WaitingPlanApproval": {
        if (opts.approvePlans === false) return { task: t, trace, humanActions };
        const plan = await api.plans.latest(t.id);
        if (!plan) throw new Error("task waits for plan approval but no plan exists");
        await api.plans.review(t.id, plan.id, "approve");
        humanActions.push(`approved plan ${plan.id}`);
        break;
      }
      case "WaitingCodeReview": {
        if (opts.approveReviews === false) return { task: t, trace, humanActions };
        await api.pr.review(t.id, "approve", "e2e: approved");
        humanActions.push("approved code review");
        if (opts.mergeWhenApproved ?? true) {
          // Give the platform a moment to record the review; merge if the task is still waiting.
          const after = await followTask(api, t.id, { deadlineMs: 90_000, until: { lanes: ["Done"] } }).catch(() => undefined);
          if (after && after.task.currentLane !== "Done" && !after.task.isMerged) {
            await api.pr.merge(t.id).catch((e) => humanActions.push(`merge call: ${(e as Error).message}`));
            humanActions.push("requested merge");
          }
        }
        break;
      }
      case "WaitingAnswer": {
        const answer = opts.answerQuestions ? await opts.answerQuestions(t) : "Use your best judgement and proceed with the simplest option.";
        await api.tasks.messages.post(t.id, answer);
        humanActions.push("answered questions");
        break;
      }
      case "WaitingUserVerification":
      case "WaitingQaReview":
        return { task: t, trace, humanActions };
      case "WaitingForRunner":
      case "OutOfTokens":
      case "ReasoningTimeout":
      case "Blocked":
        return { task: t, trace, humanActions };
      default:
        // Transient: keep following.
        break;
    }
    await new Promise((r) => setTimeout(r, 5000));
  }
}

export function summarizeTrace(trace: LaneTrace[]): string {
  return trace.map((t) => `${t.lane}/${t.status}`).join(" → ");
}

/** After Done, the merge processor lands the PR asynchronously; wait for `isMerged` (or a failure reason). */
export async function waitForMerged(api: OrkkaApi, taskId: string, deadlineMs = 5 * 60_000): Promise<TaskResponse> {
  return pollUntil<TaskResponse>({
    label: `task ${taskId} merged`,
    deadlineMs,
    intervalMs: 5000,
    fetch: () => api.tasks.get(taskId),
    done: (t) => t.isMerged || Boolean(t.mergeFailureReason),
  });
}
