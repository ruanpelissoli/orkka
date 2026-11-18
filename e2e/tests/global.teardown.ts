import path from "node:path";
import { cfg, env, paths } from "../config/index.js";
import { createApi } from "../src/auth/api-key.js";
import { stopStack } from "../src/local/stack.js";
import { listE2eProjects, readCreatedProjects, sweepE2eProjects } from "../src/safety/sweep.js";
import { writeJson } from "../src/util/fs.js";
import { log } from "../src/util/log.js";

export default async function globalTeardown() {
  log.step("Global teardown");
  if (cfg.configured && env.E2E_SUITE !== "offline" && !process.env.E2E_KEEP_PROJECTS) {
    const api = createApi();
    // Delete everything this run created that fixtures did not already remove, then anything
    // else named e2e-* (a crashed test cannot clean up after itself).
    const created = new Set(readCreatedProjects().map((c) => c.id));
    const first = await sweepE2eProjects(api, { olderThanMin: 0, only: created }).catch(() => ({ deleted: [] }));
    const rest = await sweepE2eProjects(api, { olderThanMin: 0 }).catch(() => ({ deleted: [] }));
    const leftovers = (await listE2eProjects(api).catch(() => [])).map((p) => `${p.name} (${p.id})`);
    writeJson(path.join(paths.current, "leftovers.json"), leftovers);
    log.info(`Teardown deleted ${first.deleted.length + rest.deleted.length} project(s); leftovers ${leftovers.length}`);
  }
  if (env.E2E_STACK_STOP_AFTER) await stopStack();
}
