import type { StreamEvent } from "@/hooks/use-task-signalr";

/**
 * Tier A of the Code View's data: what the agent's own tool calls say about the files it is
 * touching. `ToolUse` stream events carry `"{ToolName}: {tool input JSON}"` — the same JSON
 * the CLI hands the tool — so file paths, edit strings and whole written files are already in
 * the browser. This module turns that into a small vocabulary of file effects; the workspace
 * hook applies them to its model. It is deliberately conservative: an unrecognised tool or an
 * unparseable command yields nothing, never a guess.
 */

type FileEffectKind =
  /** An in-place edit with known before/after text (Edit, MultiEdit). */
  | "edit"
  /** The whole file was written (Write, NotebookEdit) — `content` when known. */
  | "write"
  /** The file was removed (rm, git rm). */
  | "delete"
  /** The file moved — `path` is the old name, `newPath` the new one. */
  | "rename"
  /** Something touched the file but the change is not knowable from the call (sed -i, redirects). */
  | "dirty";

interface FileEffect {
  kind: FileEffectKind;
  /** Repo-relative when the path could be relativized, otherwise as the tool saw it. */
  path: string;
  newPath?: string;
  oldString?: string;
  newString?: string;
  replaceAll?: boolean;
  content?: string;
}

interface ParsedToolEvent {
  tool: string;
  effects: FileEffect[];
  timestamp: string;
}

interface ToolInput {
  [key: string]: unknown;
}

const MUTATING_TOOLS = new Set(["Edit", "Write", "MultiEdit", "NotebookEdit", "Bash"]);

/** `"Edit: {...}"` → ["Edit", {...}]; null when the content is not shaped that way. */
function splitToolContent(content: string): { tool: string; input: ToolInput | null } | null {
  const colon = content.indexOf(":");
  if (colon <= 0) return null;
  const tool = content.slice(0, colon).trim();
  if (!/^[A-Za-z_][\w-]*$/.test(tool)) return null;
  const rest = content.slice(colon + 1).trim();
  if (!rest) return { tool, input: null };
  if (!rest.startsWith("{")) return { tool, input: null };
  try {
    const parsed = JSON.parse(rest);
    return { tool, input: parsed && typeof parsed === "object" ? (parsed as ToolInput) : null };
  } catch {
    return { tool, input: null };
  }
}

/**
 * Worktree paths come back absolute — `/repos/{org}/{repo}/worktrees/task-{id}/src/a.ts` on
 * the platform, `C:\dev\repo-worktrees\WAV-27\src\a.ts` from a local runner. The tree wants
 * them repo-relative. Both layouts put the working copy directly under a `…worktrees/<name>/`
 * segment, so cut there; anything else is normalized to forward slashes and left alone.
 */
function relativizePath(raw: string): string {
  const path = raw.replace(/\\/g, "/");
  const marker = path.search(/(^|\/)[^/]*worktrees\/[^/]+\//);
  if (marker >= 0) {
    const after = path.indexOf("worktrees/", marker) + "worktrees/".length;
    const slash = path.indexOf("/", after);
    if (slash >= 0 && slash + 1 < path.length) return path.slice(slash + 1);
  }
  return path.replace(/^\.\//, "");
}

function str(input: ToolInput, key: string): string | null {
  const v = input[key];
  return typeof v === "string" && v.length > 0 ? v : null;
}

/**
 * Splits a shell command line into words with just enough quoting awareness for file paths
 * (single/double quotes, backslash escapes). Not a shell — a tokenizer for the few commands
 * below.
 */
function shellWords(command: string): string[] {
  const words: string[] = [];
  let current = "";
  let quote: '"' | "'" | null = null;
  let has = false;
  for (let i = 0; i < command.length; i++) {
    const ch = command[i];
    if (quote) {
      if (ch === quote) {
        quote = null;
      } else if (ch === "\\" && quote === '"' && i + 1 < command.length) {
        current += command[++i];
      } else {
        current += ch;
      }
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      has = true;
      continue;
    }
    if (ch === "\\" && i + 1 < command.length) {
      current += command[++i];
      has = true;
      continue;
    }
    if (/\s/.test(ch)) {
      if (has || current) words.push(current);
      current = "";
      has = false;
      continue;
    }
    current += ch;
    has = true;
  }
  if (has || current) words.push(current);
  return words;
}

const PATH_LIKE = /^[^-][^\s]*$/;

/** Effects for one simple command (no pipes/chains). */
function effectsForCommand(words: string[]): FileEffect[] {
  if (words.length === 0) return [];
  // Strip leading env assignments and sudo/env wrappers
  let i = 0;
  while (i < words.length && (/^[A-Za-z_][A-Za-z0-9_]*=/.test(words[i]) || words[i] === "sudo" || words[i] === "env")) i++;
  const cmd = words[i];
  const args = words.slice(i + 1);
  const files = (from: string[]) => from.filter((w) => PATH_LIKE.test(w) && w !== "--");

  switch (cmd) {
    case "git": {
      const sub = args[0];
      const rest = args.slice(1);
      if (sub === "rm") return files(rest).map((path) => ({ kind: "delete", path: relativizePath(path) }));
      if (sub === "mv") {
        const f = files(rest);
        if (f.length >= 2) {
          const to = f[f.length - 1];
          return f.slice(0, -1).map((path) => ({ kind: "rename", path: relativizePath(path), newPath: relativizePath(to) }));
        }
        return [];
      }
      if (sub === "checkout" || sub === "restore") {
        // `git checkout -- file` / `git restore file`: contents changed back — dirty
        return files(rest.filter((w) => w !== "--" && !w.startsWith("-"))).map((path) => ({ kind: "dirty", path: relativizePath(path) }));
      }
      return [];
    }
    case "rm":
      return files(args).map((path) => ({ kind: "delete", path: relativizePath(path) }));
    case "mv": {
      const f = files(args);
      if (f.length >= 2) {
        const to = f[f.length - 1];
        return f.slice(0, -1).map((path) => ({ kind: "rename", path: relativizePath(path), newPath: relativizePath(to) }));
      }
      return [];
    }
    case "cp": {
      const f = files(args);
      return f.length >= 2 ? [{ kind: "dirty", path: relativizePath(f[f.length - 1]) }] : [];
    }
    case "touch":
      return files(args).map((path) => ({ kind: "dirty", path: relativizePath(path) }));
    case "sed": {
      // Only in-place edits touch files: sed -i / -i.bak / --in-place
      const inPlace = args.some((w) => /^(-i|--in-place)/.test(w) || /^-[a-zA-Z]*i/.test(w));
      if (!inPlace) return [];
      // Last non-flag, non-expression words are files; the expression is the first non-flag
      // arg unless -e/--expression is used.
      const nonFlags = args.filter((w) => !w.startsWith("-"));
      const hasExplicitExpr = args.some((w) => w === "-e" || w.startsWith("--expression"));
      const fileWords = hasExplicitExpr ? nonFlags.filter((w) => !/^s[/|#]|^\d|^\//.test(w)) : nonFlags.slice(1);
      return files(fileWords).map((path) => ({ kind: "dirty", path: relativizePath(path) }));
    }
    default:
      return [];
  }
}

/**
 * Bash gets the shell treatment: split on `&&`, `||`, `;`, `|` and newlines, look at each
 * simple command, and additionally treat `> file` / `>> file` redirects as writes.
 */
function effectsForBash(command: string): FileEffect[] {
  const effects: FileEffect[] = [];
  const segments = command.split(/\s*(?:&&|\|\||;|\||\n)\s*/);
  for (const segment of segments) {
    if (!segment.trim()) continue;
    // Redirects: capture the target, then remove them from the words
    const redirect = /(?:^|\s)(?:\d?>>?|&>)\s*(["']?)([^\s"']+)\1/g;
    let m: RegExpExecArray | null;
    while ((m = redirect.exec(segment)) !== null) {
      const target = m[2];
      if (target && target !== "/dev/null" && !target.startsWith("&")) {
        effects.push({ kind: "dirty", path: relativizePath(target) });
      }
    }
    const withoutRedirects = segment.replace(/(?:^|\s)(?:\d?>>?|&>)\s*(["']?)[^\s"']+\1/g, " ");
    effects.push(...effectsForCommand(shellWords(withoutRedirects)));
  }
  return effects;
}

/**
 * Parses a `ToolUse` stream event into file effects. Returns null for events that are not
 * tool uses, tools that don't touch files, or content that can't be understood.
 */
function parseToolEvent(event: StreamEvent): ParsedToolEvent | null {
  if (event.type !== "ToolUse" && event.type !== "ToolUsage") return null;

  // Prefer structured metadata when the server sends it (later phases); fall back to the
  // stringified content the orchestrator emits today.
  const meta = event.metadata ?? {};
  const split = splitToolContent(event.content);
  const tool = (typeof meta.tool === "string" && meta.tool) || split?.tool;
  if (!tool || !MUTATING_TOOLS.has(tool)) return null;
  const input = split?.input;

  const effects: FileEffect[] = [];
  const metaPath = typeof meta.filePath === "string" ? meta.filePath : null;

  switch (tool) {
    case "Edit": {
      const path = (input && str(input, "file_path")) ?? metaPath;
      if (!path) return null;
      effects.push({
        kind: "edit",
        path: relativizePath(path),
        oldString: (input && str(input, "old_string")) ?? undefined,
        newString: input && typeof input.new_string === "string" ? input.new_string : undefined,
        replaceAll: input?.replace_all === true,
      });
      break;
    }
    case "MultiEdit": {
      const path = (input && str(input, "file_path")) ?? metaPath;
      if (!path) return null;
      const edits = Array.isArray(input?.edits) ? (input!.edits as ToolInput[]) : [];
      if (edits.length === 0) {
        effects.push({ kind: "dirty", path: relativizePath(path) });
      } else {
        for (const e of edits) {
          effects.push({
            kind: "edit",
            path: relativizePath(path),
            oldString: str(e, "old_string") ?? undefined,
            newString: typeof e.new_string === "string" ? e.new_string : undefined,
            replaceAll: e.replace_all === true,
          });
        }
      }
      break;
    }
    case "Write": {
      const path = (input && str(input, "file_path")) ?? metaPath;
      if (!path) return null;
      effects.push({
        kind: "write",
        path: relativizePath(path),
        content: input && typeof input.content === "string" ? input.content : undefined,
      });
      break;
    }
    case "NotebookEdit": {
      const path = (input && (str(input, "notebook_path") ?? str(input, "file_path"))) ?? metaPath;
      if (!path) return null;
      effects.push({ kind: "write", path: relativizePath(path) });
      break;
    }
    case "Bash": {
      const command = input && str(input, "command");
      if (!command) return null;
      effects.push(...effectsForBash(command));
      break;
    }
  }

  if (effects.length === 0) return null;
  return { tool, effects, timestamp: event.timestamp };
}

export { parseToolEvent, relativizePath, shellWords, MUTATING_TOOLS };
export type { ParsedToolEvent, FileEffect, FileEffectKind };
