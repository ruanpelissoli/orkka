import fs from "node:fs";
import path from "node:path";
import { cfg, env, paths } from "../config/index.js";
import { createApi, describeAuthCheck } from "../src/auth/api-key.js";
import { runAuthCheck } from "../scripts/auth-check.js";
import { browserSession, mintApiKey } from "../src/local/stack.js";
import { assertSandboxStack } from "../src/safety/guards.js";
import { sweepE2eProjects } from "../src/safety/sweep.js";
import { ensureDir, writeJson } from "../src/util/fs.js";
import { EXIT_OPERATOR_ACTION, log } from "../src/util/log.js";

/**
 * Offline runs (no E2E_BASE_URL, or the offline suite) skip every environment check. Otherwise
 * the sandboxed Orkka must be up (`npm run stack:start`); a missing/stale API key is re-minted with
 * the sandbox's install secret and a missing/stale browser session from `./orkka open --print`.
 */
export default async function globalSetup() {
  fs.rmSync(paths.current, { recursive: true, force: true });
  ensureDir(paths.current);
  ensureDir(paths.runsDir);
  ensureDir(env.E2E_SCRATCH_DIR);

  const notes: string[] = [];
  const offline = !cfg.configured || env.E2E_SUITE === "offline";
  log.step(`Global setup — target=${offline ? "offline" : `${cfg.webUrl} (sandbox ${env.E2E_LOCAL_SANDBOX})`} suite=${env.E2E_SUITE ?? "adhoc"}`);
  if (offline) {
    notes.push("Offline run: only offline specs ran; environment-bound specs were skipped.");
    writeJson(path.join(paths.current, "notes.json"), notes);
    return;
  }

  const health = await fetch(new URL("/health", cfg.apiUrl)).catch(() => undefined);
  const service = health?.ok ? ((await health.json().catch(() => ({}))) as { service?: string }).service : undefined;
  if (service !== "orkka") {
    console.error(`\nThe sandboxed Orkka at ${cfg.apiUrl} is not running.\nOPERATOR ACTION REQUIRED → run: npm run stack:start\n`);
    process.exit(EXIT_OPERATOR_ACTION);
  }

  let auth = await runAuthCheck();
  if (auth.apiKey !== "ok") await mintApiKey(cfg.apiUrl);
  if (auth.storageState !== "ok") await browserSession();
  if (auth.apiKey !== "ok" || auth.storageState !== "ok") auth = await runAuthCheck();
  log.info(describeAuthCheck(auth));

  const api = createApi();
  const org = await assertSandboxStack(api);
  writeJson(path.join(paths.current, "org.json"), org);
  log.info(`Workspace "${org.orgName}" (${org.orgId})`);

  // E2E_KEEP_PROJECTS=1: a spec that READS what an earlier run left (the screens after the
  // workflow) must not lose it to the pre-run sweep.
  if (process.env.E2E_KEEP_PROJECTS) {
    notes.push("E2E_KEEP_PROJECTS set: no pre-run sweep, no teardown deletes.");
    writeJson(path.join(paths.current, "notes.json"), notes);
    return;
  }
  const sweep = await sweepE2eProjects(api, { olderThanMin: 30 });
  if (sweep.deleted.length) notes.push(`Pre-run sweep deleted ${sweep.deleted.length} stale e2e project(s): ${sweep.deleted.map((p) => p.name).join(", ")}`);
  if (sweep.skipped.length) notes.push(`Pre-run sweep left ${sweep.skipped.length} recent e2e project(s) alone (possible concurrent run): ${sweep.skipped.map((p) => p.name).join(", ")}`);
  writeJson(path.join(paths.current, "notes.json"), notes);
}
