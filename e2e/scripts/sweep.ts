/** Delete leftover e2e-* projects and stale scratch/local-folder dirs. `npm run sweep` / `npm run sweep:dry`. */
import { createApi } from "../src/auth/api-key.js";
import { assertSandboxStack } from "../src/safety/guards.js";
import { sweepE2eProjects, sweepLocalFixtureDirs } from "../src/safety/sweep.js";

const dryRun = process.argv.includes("--dry-run");
const api = createApi();
const org = await assertSandboxStack(api);
console.log(`Workspace: ${org.orgName} (${org.orgId}) dryRun=${dryRun}`);
const r = await sweepE2eProjects(api, { olderThanMin: 0, dryRun });
console.log(`projects: deleted=${r.deleted.length} skipped=${r.skipped.length} failed=${r.failed.length}`);
for (const f of r.failed) console.log(`  FAILED ${f.project.name}: ${f.error}`);
if (!dryRun) {
  const dirs = sweepLocalFixtureDirs(0);
  console.log(`scratch / local fixture dirs removed: ${dirs.length}`);
}
