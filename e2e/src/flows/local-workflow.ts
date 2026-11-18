/**
 * Building blocks of the developer's workflow on the LOCAL stack (tests/local/workflow.spec.ts):
 * a base folder → a project, a tracker connection added through the write-only host → runner op,
 * the ticket source, and pollers that follow tracker tasks through the pipeline.
 */
import { expect } from "@playwright/test";
import { cfg } from "../../config/index.js";
import type { OrkkaApi } from "../api/client.js";
import { pollUntil } from "../api/poll.js";
import type { TaskResponse } from "../api/types.js";
import { log } from "../util/log.js";

export interface FolderProject {
  project: { id: string; name: string; slug: string; key: string; baseFolder: string | null; baseFolderShape: string | null };
  repos: { id: string; name: string; gitUrl: string; defaultBranch: string; folderPath: string | null; location: "remote" | "local"; workspaceStatus: string }[];
  warnings: string[];
}

export async function createProjectFromFolder(api: OrkkaApi, folder: string, name: string): Promise<FolderProject> {
  return api.http.post<FolderProject>("/api/projects/from-folder", { path: folder, name }, { retry: false, timeoutMs: 5 * 60_000 });
}

export interface AddConnectionResult {
  supported: boolean;
  ok: boolean;
  error: string | null;
  connection: { id: string; key: string; kind: string; siteUrl: string; accountId: string | null; healthy: boolean } | null;
}

/** Settings → Trackers' "Add": the one host → runner call that carries a credential. */
export async function addTrackerConnection(api: OrkkaApi, body: { kind: "jira" | "ado"; site: string; email?: string; token: string }) {
  return api.http.post<AddConnectionResult>("/api/trackers/connections", body, { retry: false, timeoutMs: 120_000 });
}

/** Every agent in the project on the cheapest model the provider has (keeps real runs small). */
export async function useCheapestModel(api: OrkkaApi, projectId: string, model = "claude-haiku-4-5") {
  for (const a of (await api.agents.list(projectId)) as unknown as { id: string; model: string }[]) {
    if (a.model !== model) await api.agents.update(projectId, a.id, { name: null, avatar: null, model, userInstructions: null });
  }
}

type AnyTask = TaskResponse & { externalKey?: string | null; groupId?: string | null; storyOrder?: number | null; publishedPrUrl?: string | null; currentLane?: string; repoName?: string | null };

export async function listTasks(api: OrkkaApi, projectId: string): Promise<AnyTask[]> {
  const r = await api.tasks.list(projectId);
  return (Array.isArray(r) ? r : r.items) as AnyTask[];
}

/** The task a tracker ticket became (polls through the runner's sync, ≤ a few minutes). */
export async function waitForTicketTask(api: OrkkaApi, projectId: string, key: string, deadlineMs = cfg.timeouts.trackerPollMs + 5 * 60_000): Promise<AnyTask> {
  return pollUntil({
    label: `a task for ${key}`,
    deadlineMs,
    intervalMs: 5_000,
    fetch: async () => (await listTasks(api, projectId)).find((t) => t.externalKey === key),
    done: (t) => Boolean(t),
  }) as Promise<AnyTask>;
}

/** Follow a task until `until(task)` holds; logs every lane/status change. */
export async function followUntil(api: OrkkaApi, taskId: string, until: (t: AnyTask) => boolean, label: string, deadlineMs = 30 * 60_000): Promise<AnyTask> {
  let last = "";
  return pollUntil({
    label,
    deadlineMs,
    intervalMs: 10_000,
    // Up to 5 minutes of a stopped Orkka (a stop/restart mid-run, e.g. the shutdown check).
    tolerateErrors: 30,
    fetch: async () => {
      const t = (await api.tasks.get(taskId)) as AnyTask;
      const now = `${t.currentLane}/${t.status}`;
      if (now !== last) log.info(`${t.code}: ${now}`);
      last = now;
      return t;
    },
    fail: (t) => (/Failed|Blocked/.test(String(t.status)) ? `${t.code} is ${t.status} in ${t.currentLane}` : undefined),
    done: until,
  }) as Promise<AnyTask>;
}

/** Ask the task's Discuss chat one question; the reply streams as SSE (`data: {"text":…}`). */
export async function discuss(api: OrkkaApi, taskId: string, content: string): Promise<string> {
  const res = await api.http.post<Response>(`/api/tasks/${taskId}/chat`, { content }, { raw: true, retry: false, timeoutMs: 10 * 60_000 });
  expect(res.headers.get("content-type") ?? "").toContain("text/event-stream");
  let text = "";
  for (const line of (await res.text()).split(/\r?\n/)) {
    if (!line.startsWith("data:")) continue;
    const evt = JSON.parse(line.slice(5)) as { text?: string; error?: string };
    if (evt.error) throw new Error(`Discuss: ${evt.error}`);
    if (evt.text) text += evt.text;
  }
  return text;
}

/** "Apply to task": answers a pending question, rejects a plan with feedback, or posts a note. */
export async function applyToTask(api: OrkkaApi, taskId: string, body: { text?: string; messageId?: string }) {
  return api.http.post<{ lane: string; status: string; action: string }>(`/api/tasks/${taskId}/chat/apply`, body, { retry: false, timeoutMs: 120_000 });
}

/**
 * `followUntil`, answering any question the agents ask on the way with its recommended (else
 * first) option — a hands-off run must not stall in PendingQuestions (the PM may reasonably ask,
 * e.g. how div(a, 0) should behave; R5 run).
 */
export async function followAnswering(api: OrkkaApi, taskId: string, until: (t: AnyTask) => boolean, label: string, deadlineMs = 30 * 60_000): Promise<AnyTask> {
  const started = Date.now();
  for (;;) {
    const t = await followUntil(api, taskId, (x) => until(x) || x.currentLane === "PendingQuestions", label, deadlineMs - (Date.now() - started));
    if (until(t)) return t;
    const q = await pendingQuestion(api, taskId);
    const answer = q?.recommended ?? q?.options?.[0] ?? "Keep it minimal.";
    log.info(`${t.code} asks "${q?.question ?? "?"}" → answering "${answer}"`);
    await applyToTask(api, taskId, { text: answer });
    await new Promise((r) => setTimeout(r, 5_000));
  }
}

/** The last open question of a task, parsed from its ```questions block. */
export async function pendingQuestion(api: OrkkaApi, taskId: string): Promise<{ question: string; options: string[]; recommended?: string } | undefined> {
  const msgs = (await api.tasks.messages.list(taskId)) as unknown as { messageType: string; content: string }[];
  const q = [...msgs].reverse().find((m) => m.messageType === "Question");
  const m = q && /```questions\s*([\s\S]*?)```/.exec(q.content);
  if (!m) return undefined;
  return (JSON.parse(m[1]) as { questions: { question: string; options: string[]; recommended?: string }[] }).questions[0];
}

export async function messagesText(api: OrkkaApi, taskId: string): Promise<{ messageType: string; content: string }[]> {
  return (await api.tasks.messages.list(taskId)) as unknown as { messageType: string; content: string }[];
}
