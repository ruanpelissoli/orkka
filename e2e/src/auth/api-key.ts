import { cfg, paths } from "../../config/index.js";
import { OrkkaApi } from "../api/client.js";
import { installSecret } from "../local/stack.js";
import { readJson } from "../util/fs.js";

export interface SavedApiKey {
  key: string;
  keyId: string;
  name: string;
  createdAt: string;
  permissions: string[];
}

export const HARNESS_KEY_PREFIX = "e2e-harness-";

export function loadApiKey(): SavedApiKey | undefined {
  return readJson<SavedApiKey>(paths.apiKey);
}

/** The API façade for the local stack, authenticated with the harness's `orkka_key_` API key. */
export function createApi(): OrkkaApi {
  if (!cfg.configured) throw new Error("No local stack configured — set E2E_BASE_URL=http://127.0.0.1:6750 in .env");
  const saved = loadApiKey();
  if (!saved) throw new Error(`No API key at ${paths.apiKey}. Run: npm run stack:start (or stack:auth)`);
  return OrkkaApi.create(cfg.apiUrl, { kind: "api-key", key: saved.key }, cfg.rateLimit);
}

/**
 * The install secret as a bearer: the local user's root credential (what the UI's server side
 * and the CLI use). Needed for what an API key may not do — e.g. mint API keys.
 */
export function createOwnerApi(): OrkkaApi {
  return OrkkaApi.create(cfg.apiUrl, { kind: "bearer", getToken: async () => installSecret() }, cfg.rateLimit);
}

export type AuthCheck = {
  apiKey: "ok" | "missing" | "expired";
  storageState: "ok" | "missing" | "expired";
  orgId?: string;
  orgName?: string;
};

export function describeAuthCheck(c: AuthCheck): string {
  const parts = [`api-key=${c.apiKey}`, `storage-state=${c.storageState}`];
  if (c.orgName) parts.push(`workspace="${c.orgName}" (${c.orgId})`);
  return `AUTH: ${parts.join(" ")}`;
}

export async function checkApiKey(): Promise<Pick<AuthCheck, "apiKey" | "orgId" | "orgName">> {
  const saved = loadApiKey();
  if (!saved) return { apiKey: "missing" };
  const api = OrkkaApi.create(cfg.apiUrl, { kind: "api-key", key: saved.key }, cfg.rateLimit);
  try {
    const me = await api.org.me();
    return { apiKey: "ok", orgId: me.orgId, orgName: me.orgName };
  } catch (e) {
    const status = (e as { status?: number }).status;
    if (status === 401 || status === 403) return { apiKey: "expired" };
    throw e;
  }
}
