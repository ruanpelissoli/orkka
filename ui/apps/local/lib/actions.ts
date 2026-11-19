import { api, ApiError, extractErrorMessage, invalidateApiCache } from "@/lib/api";
import { refreshData } from "@/lib/data-refresh";
import type { PriorityKey, TaskTypeKey } from "@/lib/constants";
import type {
  WorkspaceAiProviderChoice,
  UpdateTaskRequest,
  ReviewPlanRequest,
  PrComment,
  UpdateDiagramRequest,
  CreateDocumentRequest,
  UpdateDocumentRequest,
  ProjectDocumentDetail,
} from "@/lib/types";

/**
 * The UI's mutations, called from client components in the browser.
 *
 * These were Next.js server actions. They now call the host directly, same-origin: the browser's
 * httpOnly `orkka_s` session cookie authenticates, so the token is empty and the API client
 * sends no Authorization header. After a write each one calls `refreshData()` (lib/data-refresh),
 * which makes every mounted page query refetch — the replacement for `revalidatePath`.
 *
 * None of them navigates. Where a server action used to `redirect()`, the function returns the
 * unprefixed target path and the caller navigates with the locale-aware router from
 * `@/i18n/navigation`.
 *
 * Expected failures are still RETURNED (`{ error }`) rather than thrown where callers show the
 * API's own reason ("runner offline", "already merged") verbatim.
 */
const token = "";

// ── Projects ──────────────────────────────────────────────────────────

export async function updateProject(projectId: string, formData: FormData) {
  const name = formData.get("name") as string | null;
  const description = formData.get("description") as string | null;

  const data: Record<string, unknown> = {};
  if (name !== null) data.name = name;
  if (description !== null) data.description = description;

  await api.projects.update(projectId, data, token);
  refreshData();
}

/** Deletes the project. Returns where to go next (the caller navigates). */
export async function deleteProject(projectId: string): Promise<string> {
  await api.projects.delete(projectId, token);
  refreshData();
  return "/dashboard";
}

// ── Tasks ─────────────────────────────────────────────────────────────

function parseTaskFormData(formData: FormData) {
  const projectId = formData.get("projectId") as string;
  const repoId = formData.get("repoId") as string;
  const title = formData.get("title") as string;
  const description = formData.get("description") as string;
  const priority = formData.get("priority") as PriorityKey;
  const type = (formData.get("type") as TaskTypeKey) || "Feature";
  const tagsRaw = formData.get("tags") as string;
  const tags = tagsRaw ? tagsRaw.split(",").map((t) => t.trim()).filter(Boolean) : [];
  const contextFilesRaw = formData.get("contextFiles") as string;
  const contextFiles = contextFilesRaw ? JSON.parse(contextFilesRaw) as string[] : undefined;
  const contextDocumentIdsRaw = formData.get("contextDocumentIds") as string;
  const contextDocumentIds = contextDocumentIdsRaw
    ? (JSON.parse(contextDocumentIdsRaw) as string[])
    : undefined;
  const assignedAgentId = (formData.get("assignedAgentId") as string) || undefined;
  // Only present when the user picked a model other than the chosen agent's own default.
  const model = (formData.get("model") as string) || undefined;
  // Unchecked checkboxes are absent from the FormData entirely.
  const skipPlanning = formData.get("skipPlanning") === "true";
  // "" is the picker's "no milestone" (backlog) option — send nothing rather than an empty id.
  const milestoneId = (formData.get("milestoneId") as string) || undefined;
  // Other repos this one piece of work also changes. getAll, not get: the field is a set of
  // checkboxes, and get() would silently keep only the first one ticked.
  const spansRepoIds = formData.getAll("spansRepoIds").map(String).filter(Boolean);
  return { projectId, repoId, title, description, priority, type, tags, contextFiles, contextDocumentIds, assignedAgentId, model, skipPlanning, milestoneId, spansRepoIds };
}

/** Creates a task and returns it — the caller uploads attachments, then navigates. */
export async function createTaskAndReturn(
  formData: FormData
): Promise<{ id: string; number: number; projectId: string }> {
  const data = parseTaskFormData(formData);
  const task = await api.tasks.create(data, token);
  refreshData();
  return { id: task.id, number: task.number, projectId: data.projectId };
}

export async function updateTask(
  taskId: string,
  data: UpdateTaskRequest
) {
  await api.tasks.update(taskId, data, token);
  refreshData();
}

export async function addTaskDependency(taskId: string, dependsOnTaskId: string) {
  const result = await api.tasks.addDependency(taskId, { dependsOnTaskId }, token);
  refreshData();
  return result;
}

export async function removeTaskDependency(taskId: string, dependsOnTaskId: string) {
  await api.tasks.removeDependency(taskId, dependsOnTaskId, token);
  refreshData();
}

/** Deletes the task. Returns where to go next — its board (the caller navigates). */
export async function deleteTask(taskId: string, projectId: string): Promise<string> {
  await api.tasks.delete(taskId, token);
  refreshData();
  return `/p/${projectId}/board`;
}

export async function submitTask(
  taskId: string
): Promise<{ error: string | null }> {
  try {
    await api.tasks.submit(taskId, token);
  } catch (e) {
    // Returned (not thrown): pre-flight blockers like "no AI provider connected"
    // must reach the user verbatim.
    return { error: extractErrorMessage(e, "Failed to submit the task. Please try again.") };
  }
  refreshData();
  return { error: null };
}

export async function resetTask(
  taskId: string
): Promise<{ error: string | null }> {
  try {
    await api.tasks.reset(taskId, token);
  } catch (e) {
    // Returned (not thrown): guard rejections like "already merged" must reach
    // the user verbatim.
    return { error: extractErrorMessage(e, "Failed to reset the task. Please try again.") };
  }
  refreshData();
  return { error: null };
}

export async function moveTaskLane(taskId: string, targetLane: string) {
  await api.tasks.moveLane(taskId, targetLane, token);
  refreshData();
}

export async function archiveTask(taskId: string) {
  await api.tasks.archive(taskId, token);
  refreshData();
}

export async function unarchiveTask(taskId: string) {
  await api.tasks.unarchive(taskId, token);
  refreshData();
}

export async function blockTask(taskId: string) {
  await api.tasks.block(taskId, token);
  refreshData();
}

export async function retryTask(taskId: string) {
  await api.tasks.retry(taskId, token);
  refreshData();
}

// ── Plans ─────────────────────────────────────────────────────────────

export async function reviewPlan(
  taskId: string,
  planId: string,
  decision: ReviewPlanRequest["decision"],
  feedback: string | undefined
) {
  await api.plans.review(taskId, planId, { decision, feedback }, token);
  refreshData();
}

// ── Verification ──────────────────────────────────────────────────────

export async function submitVerification(
  taskId: string,
  decision: "approve" | "request_changes",
  feedback?: string
) {
  await api.tasks.submitVerification(taskId, { decision, feedback }, token);
  refreshData();
}

// ── Messages ──────────────────────────────────────────────────────────

export async function sendMessage(taskId: string, content: string) {
  await api.messages.send(taskId, { content }, token);
}

// Spend checkpoint answers (see api.tasks.spendContinue / spendStop).
export async function continueTaskSpend(taskId: string, projectId?: string) {
  await api.tasks.spendContinue(taskId, token);
  if (projectId) {
    refreshData();
  }
}

export async function stopTaskSpend(taskId: string, projectId?: string) {
  await api.tasks.spendStop(taskId, token);
  if (projectId) {
    refreshData();
  }
}

// ── Interactive sessions ──────────────────────────────────────────────

/**
 * Opens an interactive session. The session is a real task from this call onward, so the board
 * shows it immediately rather than after the fact. On success returns the session's task page
 * as `path`; the caller navigates there.
 */
export async function startInteractiveSession(
  projectId: string,
  repoId: string,
  title?: string
): Promise<{ error: string; path?: undefined } | { error: null; path: string }> {
  let task;
  try {
    task = await api.tasks.startInteractiveSession(projectId, { repoId, title }, token);
  } catch (e) {
    // Returned, not thrown: blockers like "that repo isn't in this project" must reach the
    // user verbatim.
    return { error: extractErrorMessage(e, "Failed to start the session. Please try again.") };
  }
  refreshData();
  return { error: null, path: `/p/${projectId}/tasks/${task.number}` };
}

/**
 * Rules on a command an interactive session paused for.
 *
 * Returns the error rather than throwing so a 409 — someone else answered, or the prompt
 * expired — reaches the user as text instead of a thrown failure. Reporting
 * success on a decision that did not take effect is the one outcome worth avoiding here.
 */
export async function decideApproval(
  taskId: string,
  approvalId: string,
  decision: "allow" | "allow_session" | "deny"
): Promise<{ error: string | null }> {
  try {
    await api.tasks.decideApproval(taskId, approvalId, decision, token);
  } catch (e) {
    return { error: extractErrorMessage(e, "That prompt was already answered or has expired.") };
  }
  return { error: null };
}

/**
 * Ends a session and ships it. Returns once the request is recorded — the worker driving the
 * session still has to commit and push, so the task keeps moving after this resolves.
 */
export async function finishInteractiveSession(
  taskId: string
): Promise<{ error: string | null }> {
  try {
    await api.tasks.finishInteractiveSession(taskId, token);
  } catch (e) {
    return { error: extractErrorMessage(e, "Failed to finish the session. Please try again.") };
  }
  refreshData();
  return { error: null };
}

// ── Attachments ───────────────────────────────────────────────────────

export async function getAttachmentUploadUrl(
  taskId: string,
  fileName: string,
  contentType: string
): Promise<{ uploadUrl: string; blobName: string }> {
  return api.attachments.getUploadUrl(taskId, { fileName, contentType }, token);
}

export async function confirmAttachment(
  taskId: string,
  blobName: string,
  fileName: string,
  contentType: string,
  fileSize: number
) {
  await api.attachments.confirm(taskId, { blobName, fileName, contentType, fileSize }, token);
}

export async function deleteAttachment(attachmentId: string) {
  await api.attachments.delete(attachmentId, token);
  refreshData();
}

// ── Repos ─────────────────────────────────────────────────────────────

export async function removeRepo(repoId: string) {
  await api.repos.remove(repoId, token);
  refreshData();
}

// ── Agents ───────────────────────────────────────────────────────────

export async function updateAgent(projectId: string, agentId: string, formData: FormData) {
  const name = (formData.get("name") as string) || undefined;
  const model = (formData.get("model") as string) || undefined;
  const userInstructions = formData.get("userInstructions") as string | null;
  // Repo scope: the checkbox group renders a marker so "none checked" ([] = all repos)
  // is distinguishable from "group not rendered" (undefined = unchanged).
  const repoIds = formData.get("repoIdsPresent") === "1"
    ? formData.getAll("repoIds").map(String).filter(Boolean)
    : undefined;
  // Skills: same marker contract — [] clears, undefined leaves unchanged.
  const skillIds = formData.get("skillIdsPresent") === "1"
    ? formData.getAll("skillIds").map(String).filter(Boolean)
    : undefined;

  await api.agents.update(
    projectId,
    agentId,
    { name, model, userInstructions: userInstructions ?? undefined, repoIds, skillIds },
    token
  );
  refreshData();
}

/** Pins one model on every agent of the project; returns what changed. */
export async function setAgentsModel(projectId: string, model: string) {
  const result = await api.agents.setModel(projectId, model, token);
  refreshData();
  return result;
}

/** Sets the workspace's AI provider (and, optionally, switches every project's team to it). */
export async function setWorkspaceAiProvider(
  provider: WorkspaceAiProviderChoice,
  applyToExistingProjects: boolean
) {
  const result = await api.org.setAiProvider(provider, applyToExistingProjects, token);
  invalidateApiCache();
  refreshData();
  return result;
}

export async function deleteAgent(projectId: string, agentId: string) {
  await api.agents.delete(projectId, agentId, token);
  refreshData();
}

// ── Diagrams ────────────────────────────────────────────────────────────

export async function updateDiagram(
  projectId: string,
  diagramId: string,
  data: UpdateDiagramRequest
) {
  await api.diagrams.update(projectId, diagramId, data, token);
  refreshData();
}

export async function deleteDiagram(projectId: string, diagramId: string) {
  await api.diagrams.delete(projectId, diagramId, token);
  refreshData();
}

export async function restoreDiagramVersion(
  projectId: string,
  diagramId: string,
  version: number
) {
  const diagram = await api.diagrams.restoreVersion(projectId, diagramId, version, token);
  refreshData();
  return diagram;
}

// ── Design Assets ───────────────────────────────────────────────────────

export async function approveDesignAsset(projectId: string, assetId: string) {
  await api.designAssets.approve(projectId, assetId, token);
  refreshData();
}

/** Rolls the lineage back by copying this version's content into a new latest version. */
export async function restoreDesignAsset(projectId: string, assetId: string) {
  await api.designAssets.restore(projectId, assetId, token);
  refreshData();
}

export async function rejectDesignAsset(
  projectId: string,
  assetId: string,
  feedback?: string
) {
  await api.designAssets.reject(projectId, assetId, token, feedback);
  refreshData();
}

export async function deleteDesignAsset(projectId: string, assetId: string) {
  await api.designAssets.delete(projectId, assetId, token);
  refreshData();
}

export async function moveDesignAsset(projectId: string, assetId: string, folderPath: string) {
  await api.designAssets.move(projectId, assetId, folderPath, token);
  refreshData();
}

export async function renameDesignFolder(projectId: string, fromPath: string, toPath: string) {
  await api.designAssets.renameFolder(projectId, fromPath, toPath, token);
  refreshData();
}

export async function deleteDesignFolder(
  projectId: string,
  path: string,
  deleteContents: boolean
) {
  await api.designAssets.deleteFolder(projectId, path, deleteContents, token);
  refreshData();
}

export async function moveDiagram(projectId: string, diagramId: string, folderPath: string) {
  await api.diagrams.move(projectId, diagramId, folderPath, token);
  refreshData();
}

export async function renameDiagramFolder(projectId: string, fromPath: string, toPath: string) {
  await api.diagrams.renameFolder(projectId, fromPath, toPath, token);
  refreshData();
}

export async function renameDocumentFolder(projectId: string, fromPath: string, toPath: string) {
  await api.documents.renameFolder(projectId, fromPath, toPath, token);
  refreshData();
}

/** SVG markup for an asset (kind "Svg" only) — the client rasterizes it to PNG. */
export async function getDesignAssetSvg(
  projectId: string,
  assetId: string
): Promise<string | null> {
  const asset = await api.designAssets.get(projectId, assetId, token);
  return asset.svgText ?? null;
}

// ── Documents ───────────────────────────────────────────────────────────

// Create/update return null on a title conflict (409) — callers branch on the return value
// rather than catching an ApiError.
export async function createDocument(
  projectId: string,
  data: CreateDocumentRequest
): Promise<ProjectDocumentDetail | null> {
  try {
    const doc = await api.documents.create(projectId, data, token);
    refreshData();
    return doc;
  } catch (err) {
    if (err instanceof ApiError && err.status === 409) return null;
    throw err;
  }
}

export async function updateDocument(
  projectId: string,
  documentId: string,
  data: UpdateDocumentRequest
): Promise<ProjectDocumentDetail | null> {
  try {
    const doc = await api.documents.update(projectId, documentId, data, token);
    refreshData();
    return doc;
  } catch (err) {
    if (err instanceof ApiError && err.status === 409) return null;
    throw err;
  }
}

export async function deleteDocument(projectId: string, documentId: string) {
  await api.documents.delete(projectId, documentId, token);
  refreshData();
}

export async function restoreDocumentRevision(
  projectId: string,
  documentId: string,
  version: number
): Promise<ProjectDocumentDetail> {
  const doc = await api.documents.restoreRevision(projectId, documentId, version, token);
  refreshData();
  return doc;
}

// ── Pull Requests ─────────────────────────────────────────────────────

export async function addPrComment(
  taskId: string,
  path: string,
  body: string,
  line?: number
): Promise<PrComment | null> {
  try {
    return await api.pullRequests.addComment(taskId, { path, line, body }, token);
  } catch {
    return null;
  }
}

/**
 * Submits a PR review. Returns the failure rather than throwing, so the reviewer sees the API's
 * reason — a swallowed throw is how a plain 400 (a wrong decision string) once went unnoticed.
 */
export async function submitPrReview(
  taskId: string,
  decision: string,
  body?: string,
  comments?: { path: string; line: number; body: string }[],
  projectId?: string
): Promise<{ error: string | null }> {
  try {
    await api.pullRequests.submitReview(taskId, { decision, body, comments }, token);
  } catch (e) {
    return { error: extractErrorMessage(e, "Failed to submit the review. Please try again.") };
  }
  if (projectId) {
    refreshData();
  }
  return { error: null };
}

export async function mergePr(taskId: string) {
  let result;
  try {
    result = await api.pullRequests.merge(taskId, token);
  } catch (e) {
    // Returned, not thrown: the endpoint's reasons ("branch never pushed", "runner offline")
    // must reach the user verbatim.
    return {
      merged: false,
      resolutionStarted: false,
      message: null as string | null,
      error: extractErrorMessage(e, "Merge failed. Please try again."),
    };
  }
  refreshData();
  return {
    merged: !result?.resolutionStarted,
    resolutionStarted: result?.resolutionStarted ?? false,
    message: result?.message ?? null,
    error: null as string | null,
  };
}

export async function syncPr(taskId: string) {
  let result;
  try {
    result = await api.pullRequests.sync(taskId, token);
  } catch (e) {
    return { result: null, error: extractErrorMessage(e, "Could not sync with the repository.") };
  }
  refreshData();
  return { result, error: null as string | null };
}

export async function resolveModelRecommendation(
  taskId: string,
  action: "apply" | "dismiss"
) {
  let task;
  try {
    task = await api.tasks.resolveModelRecommendation(taskId, action, token);
  } catch (e) {
    // Returned, not thrown: "no longer available" must reach the user verbatim.
    return { task: null, error: extractErrorMessage(e, "Could not update the model suggestion.") };
  }
  refreshData();
  return { task, error: null as string | null };
}

export async function createTaskPr(taskId: string) {
  try {
    await api.pullRequests.create(taskId, token);
  } catch (e) {
    return { error: extractErrorMessage(e, "Could not create the pull request.") };
  }
  refreshData();
  return { error: null as string | null };
}
