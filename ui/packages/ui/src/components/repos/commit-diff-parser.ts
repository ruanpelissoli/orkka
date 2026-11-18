import type { PrFile } from "@/lib/types";

/**
 * Splits raw `git show` output (header lines followed by `diff --git`
 * sections) into PrFile-shaped objects so commit diffs can be rendered with
 * the same DiffFileCard used for pull requests.
 */
export function parseCommitDiff(raw: string): PrFile[] {
  const start = raw.indexOf("diff --git ");
  if (start === -1) return [];

  const sections = raw
    .slice(start)
    .split(/\r?\n(?=diff --git )/);

  const files: PrFile[] = [];

  for (const section of sections) {
    const lines = section.split(/\r?\n/);

    let oldPath: string | null = null;
    let newPath: string | null = null;
    let patchStart = -1;

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      if (line.startsWith("--- ")) {
        const p = line.slice(4).trim();
        oldPath = p === "/dev/null" ? null : p.replace(/^a\//, "");
      } else if (line.startsWith("+++ ")) {
        const p = line.slice(4).trim();
        newPath = p === "/dev/null" ? null : p.replace(/^b\//, "");
      } else if (line.startsWith("@@")) {
        patchStart = i;
        break;
      }
    }

    // Fall back to the `diff --git a/x b/y` header (binary files, pure
    // renames, and mode-only changes have no ---/+++ lines).
    let filename = newPath ?? oldPath;
    if (!filename) {
      const header = lines[0].match(/^diff --git a\/(.+) b\/(.+)$/);
      filename = header ? header[2] : lines[0].replace("diff --git ", "");
    }

    const isNew = oldPath === null && newPath !== null;
    const isDeleted = newPath === null && oldPath !== null;

    let additions = 0;
    let deletions = 0;
    let patch: string | null = null;

    if (patchStart !== -1) {
      const patchLines = lines.slice(patchStart);
      for (const line of patchLines) {
        if (line.startsWith("+") && !line.startsWith("+++")) additions++;
        else if (line.startsWith("-") && !line.startsWith("---")) deletions++;
      }
      patch = patchLines.join("\n");
    }

    files.push({
      filename,
      status: isNew ? "added" : isDeleted ? "removed" : "modified",
      additions,
      deletions,
      patch,
      previousFilename: null,
    });
  }

  return files;
}
