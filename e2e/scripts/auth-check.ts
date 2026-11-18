/** Prints one line describing auth validity; exit 3 when the operator has to act. `npm run auth:check`. */
import { cfg } from "../config/index.js";
import { EXIT_OPERATOR_ACTION, EXIT_SKIPPED } from "../src/util/log.js";
import { checkApiKey, describeAuthCheck, type AuthCheck } from "../src/auth/api-key.js";
import { openSignedInPage, storageStateAgeHours, storageStateExists } from "../src/auth/storage-state.js";

export async function runAuthCheck(opts: { skipBrowser?: boolean } = {}): Promise<AuthCheck> {
  const key = await checkApiKey();
  let storageState: AuthCheck["storageState"] = "missing";
  if (storageStateExists()) {
    if (opts.skipBrowser) storageState = "ok";
    else {
      const r = await openSignedInPage();
      storageState = r.ok ? "ok" : "expired";
      await r.browser.close();
    }
  }
  return { ...key, storageState };
}

const isMain = process.argv[1]?.replace(/\\/g, "/").endsWith("scripts/auth-check.ts");
if (isMain && !cfg.configured) {
  console.log("AUTH: no local stack configured (E2E_BASE_URL unset) — only offline specs can run.");
  process.exit(EXIT_SKIPPED);
}
if (isMain) {
  runAuthCheck()
    .then((c) => {
      const age = storageStateAgeHours();
      console.log(describeAuthCheck(c) + (age !== undefined ? ` (session saved ${age.toFixed(1)} h ago)` : ""));
      if (c.apiKey !== "ok" || c.storageState !== "ok") {
        console.error("→ run: npm run stack:auth (mints a key + a launch-code session from the sandbox's install secret)");
        process.exit(EXIT_OPERATOR_ACTION);
      }
    })
    .catch((e) => {
      console.error(e);
      process.exit(1);
    });
}
