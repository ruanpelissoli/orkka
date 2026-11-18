import { createApi } from "../src/auth/api-key.js";
import { anyProviderReady } from "../src/runner/presence.js";

const api = createApi();
const r = await api.me.runner();
console.log(
  JSON.stringify(
    {
      online: r.online,
      machine: r.machineName,
      version: r.daemonVersion,
      lastSeenAt: r.lastSeenAt,
      aiProviderReady: anyProviderReady(r),
      capabilities: r.capabilities,
      providers: (r.providers ?? r.lastKnownProviders ?? []).map((p) => `${p.provider}:${p.cliFound ? "cli" : "no-cli"}/${p.authenticated ? "auth" : "no-auth"}`),
      forgeClis: (r.forgeClis ?? []).map((c) => `${c.cli}:${c.found ? "found" : "missing"}/${c.authenticated ? "signed-in" : "signed-out"}${c.accounts?.length ? ` (${c.accounts.join(", ")})` : ""}`),
    },
    null,
    2,
  ),
);
