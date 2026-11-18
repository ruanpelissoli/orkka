import type { PrFile } from "@/lib/types";
import type { FileEffect, ParsedToolEvent } from "./tool-event-parser";

/**
 * The Code View's picture of a task's working copy: one entry per file the agent has
 * changed, built from a **baseline** (a stored code-change snapshot or the PR diff — the
 * truth as of the last capture) plus **live effects** parsed from the agent's tool calls
 * since then (Tier A: instant, not authoritative). Later phases replace the live layer with
 * a worker-computed working-tree diff; the model shape stays the same.
 *
 * Pure functions over an immutable model — the hook owns state and side effects.
 */

type FileStatus = "added" | "modified" | "removed" | "renamed";

interface LiveEdit {
  kind: "edit" | "write" | "dirty";
  tool: string;
  at: string;
  oldString?: string;
  newString?: string;
  replaceAll?: boolean;
  content?: string;
}

interface WorkspaceFile {
  path: string;
  status: FileStatus;
  previousPath: string | null;
  /** From the baseline; null when the file is only known from live effects. */
  patch: string | null;
  additions: number;
  deletions: number;
  /** Line-count estimate of the live edits — shown as "+n −m" only while no baseline covers them. */
  liveAdditions: number;
  liveDeletions: number;
  liveEdits: LiveEdit[];
  /** ISO timestamp of the most recent live effect, null when the file is baseline-only. */
  touchedAt: string | null;
  lastTool: string | null;
}

/**
 * Where the baseline layer came from: nothing yet, a permanent end-of-phase snapshot, the PR
 * diff, or the worker's *live* working-tree snapshot of a run in progress (Tier B — the exact
 * diff, refreshed after every mutating tool call and pushed as WorkspaceChanged).
 */
type BaselineSource = "none" | "snapshot" | "pr" | "live";

interface WorkspaceBaseline {
  source: BaselineSource;
  branchName: string | null;
  baseBranch: string | null;
  capturedAt: string | null;
  truncated: boolean;
  /** Live captures are numbered; a stale WorkspaceChanged (lower number) is ignored. */
  revision: number;
  /** The diff is read from the developer's runner (Orkka keeps no copy) and it could not be
   * read right now — offline machine, or a branch that is gone. */
  unavailable?: boolean;
}

interface WorkspaceModel {
  files: Record<string, WorkspaceFile>;
  baseline: WorkspaceBaseline;
  /** Bumps on every change — cheap dependency for memoized derivations. */
  revision: number;
  /** The file the agent touched most recently — the "agent is here" cursor. */
  activePath: string | null;
  /** Timestamp of the newest live effect applied. */
  lastLiveAt: string | null;
}

const EMPTY_BASELINE: WorkspaceBaseline = {
  source: "none",
  branchName: null,
  baseBranch: null,
  capturedAt: null,
  truncated: false,
  revision: 0,
};

function emptyModel(): WorkspaceModel {
  return { files: {}, baseline: EMPTY_BASELINE, revision: 0, activePath: null, lastLiveAt: null };
}

function normalizeStatus(status: string): FileStatus {
  switch (status) {
    case "added":
    case "removed":
    case "renamed":
      return status;
    case "deleted":
      return "removed";
    case "copied":
      return "added";
    default:
      return "modified";
  }
}

function fileFromBaseline(pr: PrFile): WorkspaceFile {
  return {
    path: pr.filename,
    status: normalizeStatus(pr.status),
    previousPath: pr.previousFilename,
    patch: pr.patch,
    additions: pr.additions,
    deletions: pr.deletions,
    liveAdditions: 0,
    liveDeletions: 0,
    liveEdits: [],
    touchedAt: null,
    lastTool: null,
  };
}

/**
 * Replace the baseline layer. Live edits that arrived after the baseline's capture time are
 * kept (they describe changes the capture cannot contain); older ones are folded away, since
 * the baseline now covers them.
 */
function withBaseline(
  model: WorkspaceModel,
  files: PrFile[],
  baseline: WorkspaceBaseline
): WorkspaceModel {
  const next: Record<string, WorkspaceFile> = {};
  for (const pr of files) next[pr.filename] = fileFromBaseline(pr);

  const capturedMs = baseline.capturedAt ? Date.parse(baseline.capturedAt) : Number.NEGATIVE_INFINITY;
  for (const existing of Object.values(model.files)) {
    const fresh = existing.liveEdits.filter((e) => Date.parse(e.at) > capturedMs);
    if (fresh.length === 0) continue;
    const base = next[existing.path] ?? {
      ...existing,
      patch: null,
      additions: 0,
      deletions: 0,
      // Unknown to the baseline: it exists only because a tool touched it
      status: existing.status,
    };
    const counts = countLive(fresh);
    next[existing.path] = {
      ...base,
      liveEdits: fresh,
      liveAdditions: counts.additions,
      liveDeletions: counts.deletions,
      touchedAt: fresh[fresh.length - 1].at,
      lastTool: fresh[fresh.length - 1].tool,
    };
  }

  return {
    ...model,
    files: next,
    baseline,
    revision: model.revision + 1,
    activePath: model.activePath && next[model.activePath] ? model.activePath : null,
  };
}

function lineCount(text: string | undefined): number {
  if (!text) return 0;
  const n = text.split("\n").length;
  return text.endsWith("\n") ? n - 1 : n;
}

function countLive(edits: LiveEdit[]): { additions: number; deletions: number } {
  let additions = 0;
  let deletions = 0;
  for (const e of edits) {
    if (e.kind === "edit") {
      additions += lineCount(e.newString);
      deletions += lineCount(e.oldString);
    } else if (e.kind === "write") {
      additions += lineCount(e.content);
    }
  }
  return { additions, deletions };
}

function ensureFile(files: Record<string, WorkspaceFile>, path: string, status: FileStatus): WorkspaceFile {
  return (
    files[path] ?? {
      path,
      status,
      previousPath: null,
      patch: null,
      additions: 0,
      deletions: 0,
      liveAdditions: 0,
      liveDeletions: 0,
      liveEdits: [],
      touchedAt: null,
      lastTool: null,
    }
  );
}

function applyEffect(
  files: Record<string, WorkspaceFile>,
  effect: FileEffect,
  tool: string,
  at: string
): string | null {
  const touch = (file: WorkspaceFile, edit: LiveEdit | null): WorkspaceFile => {
    const liveEdits = edit ? [...file.liveEdits, edit] : file.liveEdits;
    const counts = countLive(liveEdits);
    return {
      ...file,
      liveEdits,
      liveAdditions: counts.additions,
      liveDeletions: counts.deletions,
      touchedAt: at,
      lastTool: tool,
    };
  };

  switch (effect.kind) {
    case "edit": {
      const file = ensureFile(files, effect.path, "modified");
      files[effect.path] = touch(
        // A file the baseline saw as removed and that is now being edited is back
        { ...file, status: file.status === "removed" ? "modified" : file.status },
        {
          kind: "edit",
          tool,
          at,
          oldString: effect.oldString,
          newString: effect.newString,
          replaceAll: effect.replaceAll,
        }
      );
      return effect.path;
    }
    case "write": {
      const known = files[effect.path];
      const file = ensureFile(files, effect.path, "added");
      files[effect.path] = touch(
        { ...file, status: known && known.status !== "removed" ? known.status : known ? "modified" : "added" },
        { kind: "write", tool, at, content: effect.content }
      );
      return effect.path;
    }
    case "delete": {
      const file = ensureFile(files, effect.path, "removed");
      files[effect.path] = touch({ ...file, status: "removed" }, { kind: "dirty", tool, at });
      return effect.path;
    }
    case "rename": {
      if (!effect.newPath) return null;
      const from = files[effect.path];
      const base = from ?? ensureFile(files, effect.newPath, "renamed");
      delete files[effect.path];
      files[effect.newPath] = touch(
        {
          ...base,
          path: effect.newPath,
          previousPath: from ? (from.previousPath ?? effect.path) : effect.path,
          status: from && from.status === "added" ? "added" : "renamed",
        },
        { kind: "dirty", tool, at }
      );
      return effect.newPath;
    }
    case "dirty": {
      const file = ensureFile(files, effect.path, "modified");
      files[effect.path] = touch(file, { kind: "dirty", tool, at });
      return effect.path;
    }
  }
}

/** Apply one parsed tool event. Returns the same model when nothing changed. */
function applyToolEvent(model: WorkspaceModel, parsed: ParsedToolEvent): WorkspaceModel {
  const files = { ...model.files };
  let active: string | null = null;
  for (const effect of parsed.effects) {
    const touched = applyEffect(files, effect, parsed.tool, parsed.timestamp);
    if (touched) active = touched;
  }
  if (!active) return model;
  return {
    ...model,
    files,
    revision: model.revision + 1,
    activePath: active,
    lastLiveAt: parsed.timestamp,
  };
}

interface WorkspaceTotals {
  fileCount: number;
  additions: number;
  deletions: number;
  /** True when any file's numbers come from live estimates rather than a baseline. */
  hasLive: boolean;
}

function totals(model: WorkspaceModel): WorkspaceTotals {
  let additions = 0;
  let deletions = 0;
  let hasLive = false;
  const list = Object.values(model.files);
  for (const f of list) {
    additions += f.additions + f.liveAdditions;
    deletions += f.deletions + f.liveDeletions;
    if (f.liveEdits.length > 0) hasLive = true;
  }
  return { fileCount: list.length, additions, deletions, hasLive };
}

export { emptyModel, withBaseline, applyToolEvent, totals, EMPTY_BASELINE };
export type {
  WorkspaceModel,
  WorkspaceFile,
  WorkspaceBaseline,
  WorkspaceTotals,
  LiveEdit,
  FileStatus,
  BaselineSource,
};
