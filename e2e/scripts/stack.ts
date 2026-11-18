/**
 * The sandboxed Orkka: `npm run stack:start [-- --fresh]` | `stack:stop` | `stack:status` | `stack:auth`.
 * `start` runs `dotnet orkka.dll start --no-browser --port <E2E_BASE_URL's port>` in the background
 * with ORKKA_HOME=<E2E_LOCAL_SANDBOX>/home, waits for /health, mints the harness API key
 * (.auth/api-key.json) and a browser session from `open --print` (.auth/storageState.json).
 * Build first: `./orkka start` once (or `dotnet build src/Orkka -c Release` + the UI build).
 */
import { browserSession, listeners, mintApiKey, stackStatus, startStack, stopStack } from "../src/local/stack.js";
import { log } from "../src/util/log.js";

const [verb = "status", ...rest] = process.argv.slice(2);
if (verb === "start") {
  const url = await startStack({ fresh: rest.includes("--fresh") });
  await mintApiKey(url);
  await browserSession();
  log.info(await stackStatus());
} else if (verb === "auth") {
  await mintApiKey();
  await browserSession();
} else if (verb === "stop") {
  await stopStack();
  const busy = await listeners();
  log.info(busy.length ? `still listening: ${busy.join(", ")}` : "nothing listens on the sandbox port");
  if (busy.length) process.exit(1);
} else {
  log.info(await stackStatus());
}
