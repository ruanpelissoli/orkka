/**
 * Where does a secret live in the sandbox? Used to prove a tracker token typed into Settings →
 * Trackers ends up ONLY in the runner's secret store (the sandbox's owner-only file store) — not in
 * the SQLite database, not in a log, not in trackers.json.
 */
import fs from "node:fs";
import path from "node:path";
import { databaseFile, orkkaHome } from "./stack.js";

function walk(dir: string, out: string[], skip: (p: string) => boolean) {
  if (!fs.existsSync(dir)) return;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const f = path.join(dir, e.name);
    if (skip(f)) continue;
    if (e.isDirectory()) walk(f, out, skip);
    else out.push(f);
  }
}

/**
 * Files under the sandbox's Orkka home that contain `needle` — the database included (see also
 * `databaseContains`), the git cache skipped.
 */
export function filesContaining(needle: string, root = orkkaHome()): string[] {
  const files: string[] = [];
  const skipDirs = [path.join(root, "repos")];
  walk(root, files, (p) => skipDirs.some((d) => p === d));
  const hits: string[] = [];
  const bytes = Buffer.from(needle, "utf8");
  for (const f of files) {
    try {
      if (fs.statSync(f).size > 200 * 1024 * 1024) continue;
      if (fs.readFileSync(f).includes(bytes)) hits.push(f);
    } catch {
      /* locked or vanished */
    }
  }
  return hits;
}

/**
 * Does the SQLite database (`orkka.db` plus its `-wal` journal, where recent writes live until a
 * checkpoint) contain `needle`? Text columns are stored as UTF-8, so a byte search is exhaustive.
 */
export function databaseContains(needle: string): boolean {
  const bytes = Buffer.from(needle, "utf8");
  return ["", "-wal"].some((suffix) => {
    const f = databaseFile() + suffix;
    return fs.existsSync(f) && fs.readFileSync(f).includes(bytes);
  });
}
