import type { DiffHunk } from "@/lib/types";

const HUNK_HEADER_RE = /^@@\s*-(\d+),?(\d*)\s*\+(\d+),?(\d*)\s*@@(.*)?$/;

export function parsePatch(patch: string): DiffHunk[] {
  if (!patch) return [];

  const lines = patch.split("\n");
  const hunks: DiffHunk[] = [];
  let currentHunk: DiffHunk | null = null;
  let oldLine = 0;
  let newLine = 0;

  for (const line of lines) {
    const hunkMatch = line.match(HUNK_HEADER_RE);

    if (hunkMatch) {
      currentHunk = {
        oldStart: parseInt(hunkMatch[1], 10),
        oldCount: hunkMatch[2] ? parseInt(hunkMatch[2], 10) : 1,
        newStart: parseInt(hunkMatch[3], 10),
        newCount: hunkMatch[4] ? parseInt(hunkMatch[4], 10) : 1,
        header: line,
        lines: [],
      };
      hunks.push(currentHunk);
      oldLine = currentHunk.oldStart;
      newLine = currentHunk.newStart;
      continue;
    }

    if (!currentHunk) continue;

    // Skip "\ No newline at end of file" markers
    if (line.startsWith("\\")) continue;

    const prefix = line[0];
    const content = line.slice(1);

    if (prefix === "+") {
      currentHunk.lines.push({
        type: "add",
        content,
        oldLineNumber: null,
        newLineNumber: newLine++,
      });
    } else if (prefix === "-") {
      currentHunk.lines.push({
        type: "delete",
        content,
        oldLineNumber: oldLine++,
        newLineNumber: null,
      });
    } else {
      // Context line (space prefix) or any other line
      currentHunk.lines.push({
        type: "context",
        content: prefix === " " ? content : line,
        oldLineNumber: oldLine++,
        newLineNumber: newLine++,
      });
    }
  }

  return hunks;
}

/** Group comments by file path and line number for quick lookup */
export function groupCommentsByLine(
  comments: { path: string; line: number | null; [key: string]: unknown }[]
): Map<string, typeof comments> {
  const map = new Map<string, typeof comments>();
  for (const comment of comments) {
    if (!comment.line) continue;
    const key = `${comment.path}:${comment.line}`;
    const existing = map.get(key) ?? [];
    existing.push(comment);
    map.set(key, existing);
  }
  return map;
}
