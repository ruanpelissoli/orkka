import path from "node:path";
import fs from "node:fs";
import { env, paths } from "../../config/index.js";
import type { OrkkaApi } from "../api/client.js";
import type { ProjectResponse } from "../api/types.js";
import { deleteProject, isE2eName } from "./guards.js";
import { appendLine, readLines } from "../util/fs.js";
import { log } from "../util/log.js";

const createdFile = () => path.join(paths.current, "created-projects.jsonl");

/** Record a created project BEFORE any further call so a crash still leaves a cleanup trail. */
export function recordCreatedProject(p: Pick<ProjectResponse, "id" | "name">, testTitle?: string) {
  appendLine(createdFile(), JSON.stringify({ id: p.id, name: p.name, at: new Date().toISOString(), test: testTitle }));
}

export function readCreatedProjects(): { id: string; name: string; at: string; test?: string }[] {
  return readLines(createdFile()).map((l) => JSON.parse(l));
}

export async function listE2eProjects(api: OrkkaApi): Promise<ProjectResponse[]> {
  const all = await api.projects.list();
  return all.filter((p) => isE2eName(p.name));
}

export interface SweepResult {
  deleted: ProjectResponse[];
  skipped: ProjectResponse[];
  failed: { project: ProjectResponse; error: string }[];
}

/**
 * Delete `e2e-*` projects. `olderThanMin` protects a concurrent run's fresh projects; pass 0 to
 * sweep everything. Deleting a project deletes its ticket source with it; it never touches the
 * repositories (they are local bare remotes or the developer's own).
 */
export async function sweepE2eProjects(api: OrkkaApi, opts: { olderThanMin?: number; dryRun?: boolean; only?: Set<string> } = {}): Promise<SweepResult> {
  const result: SweepResult = { deleted: [], skipped: [], failed: [] };
  const projects = await listE2eProjects(api);
  const cutoff = Date.now() - (opts.olderThanMin ?? 0) * 60_000;
  for (const p of projects.filter((x) => (opts.only ? opts.only.has(x.id) : true))) {
    if (new Date(p.createdAt).getTime() > cutoff) {
      result.skipped.push(p);
      continue;
    }
    if (opts.dryRun) {
      log.info(`[dry-run] would delete project ${p.name} (${p.id})`);
      result.deleted.push(p);
      continue;
    }
    try {
      await deleteProject(api, p.id);
      log.info(`Deleted project ${p.name} (${p.id})`);
      result.deleted.push(p);
    } catch (e) {
      result.failed.push({ project: p, error: e instanceof Error ? e.message : String(e) });
      log.warn(`Failed to delete ${p.name}: ${result.failed.at(-1)!.error}`);
    }
  }
  return result;
}

/** Remove per-run local-folder fixtures older than a day. */
export function sweepLocalFixtureDirs(olderThanHours = 24): string[] {
  const removed: string[] = [];
  for (const root of [env.E2E_LOCAL_FIXTURE_PARENT, env.E2E_SCRATCH_DIR]) {
    if (!fs.existsSync(root)) continue;
    for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
      // The stub CLI bin dir is reused across runs (gh.exe is compiled once).
      if (!entry.isDirectory() || entry.name === "bin") continue;
      const full = path.join(root, entry.name);
      const age = Date.now() - fs.statSync(full).mtimeMs;
      if (age > olderThanHours * 3_600_000) {
        try {
          fs.rmSync(full, { recursive: true, force: true });
          removed.push(full);
        } catch (e) {
          log.warn(`Could not remove ${full}: ${(e as Error).message}`);
        }
      }
    }
  }
  return removed;
}
