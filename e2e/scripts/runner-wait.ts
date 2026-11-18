/** Wait until the stack's supervised runner is online with the tracker capabilities. `npm run runner:wait` */
import { cfg } from "../config/index.js";
import { createApi } from "../src/auth/api-key.js";
import { TRACKER_CAPS, waitForRunner } from "../src/runner/presence.js";

const r = await waitForRunner(createApi(), TRACKER_CAPS, cfg.timeouts.runnerOnlineMs);
console.log(`runner online=${r.online} machine=${r.machineName} version=${r.daemonVersion}`);
