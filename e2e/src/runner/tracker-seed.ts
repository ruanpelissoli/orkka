/**
 * Puts a tracker connection that points at the FAKE tracker into the sandboxed runner's
 * workspace — the equivalent of `./orkka tracker add`, without its hidden token prompt.
 *
 * The sandbox runs the runner with two test opt-ins (see `sandboxEnv`):
 *  - `ORKKA_TRACKER_ALLOW_INSECURE_SITE=1` keeps a loopback site (`http://127.0.0.1:<port>[/<org>]`)
 *    as typed instead of normalizing it to `https://{host}` / dev.azure.com;
 *  - `ORKKA_SECRET_STORE=file` makes the runner read tokens from the owner-only file store
 *    `<ORKKA_HOME>/workspaces/<workspace>/secrets/<id>` instead of the OS keychain — so the
 *    harness never touches the developer's real Credential Manager / Keychain / libsecret.
 * The harness writes `trackers.json` and that file; `removeSeededConnection` deletes both.
 */
import fs from "node:fs";
import path from "node:path";
import { env } from "../../config/index.js";
import { ensureDir } from "../util/fs.js";

export interface SeededConnection {
  id: string;
  kind: "jira" | "ado" | "github";
  siteUrl: string;
  email?: string;
  workspace: string;
  /** The ORKKA_HOME it was written under. */
  home: string;
}

function workspaceDir(workspace = env.E2E_RUNNER_WORKSPACE, home = env.E2E_RUNNER_HOME) {
  return path.join(home, "workspaces", workspace);
}

export function trackersFile(workspace = env.E2E_RUNNER_WORKSPACE, home = env.E2E_RUNNER_HOME) {
  return path.join(workspaceDir(workspace, home), "trackers.json");
}

interface TrackerEntry {
  id: string;
  kind: string;
  siteUrl: string;
  email?: string;
  /** GitHub: "gh" = the developer's gh login (no secret file); absent = a stored token. */
  auth?: string;
  accountId?: string;
  displayName?: string;
  healthy: boolean;
  healthDetail?: string;
  checkedAt?: string;
  addedAt: string;
}

export function readTrackersFile(workspace = env.E2E_RUNNER_WORKSPACE, home = env.E2E_RUNNER_HOME): TrackerEntry[] {
  const f = trackersFile(workspace, home);
  if (!fs.existsSync(f)) return [];
  const doc = JSON.parse(fs.readFileSync(f, "utf8")) as { trackers?: TrackerEntry[] };
  return doc.trackers ?? [];
}

function writeTrackersFile(entries: TrackerEntry[], workspace: string, home: string) {
  const f = trackersFile(workspace, home);
  ensureDir(path.dirname(f));
  const tmp = `${f}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify({ version: 1, trackers: entries }, null, 2));
  fs.renameSync(tmp, f);
}

function secretFile(workspace: string, home: string, id: string) {
  return path.join(workspaceDir(workspace, home), "secrets", id);
}

// The layout FileSecretStore reads: one file per tracker id, owner-only.
function storeSecret(workspace: string, home: string, id: string, secret: string) {
  const file = secretFile(workspace, home, id);
  fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
  fs.writeFileSync(file, secret, { mode: 0o600 });
}

function deleteSecret(workspace: string, home: string, id: string) {
  fs.rmSync(secretFile(workspace, home, id), { force: true });
}

/**
 * Add (or replace) a connection to the fake tracker in the sandboxed runner's workspace. A
 * running runner picks it up on its next Hello (≤ 60 s). `healthy` starts false with no
 * `checkedAt`, so the runner tests the token against the fake on that Hello.
 */
export async function seedTrackerConnection(c: { id: string; kind: "jira" | "ado" | "github"; siteUrl: string; email?: string; secret: string; workspace?: string; home?: string }): Promise<SeededConnection> {
  if (!/^[a-z0-9][a-z0-9-]{0,60}$/.test(c.id)) throw new Error(`tracker id ${c.id} must be a slug`);
  const workspace = c.workspace ?? env.E2E_RUNNER_WORKSPACE;
  const home = c.home ?? env.E2E_RUNNER_HOME;
  storeSecret(workspace, home, c.id, c.secret);
  const entries = readTrackersFile(workspace, home).filter((e) => e.id !== c.id);
  entries.push({ id: c.id, kind: c.kind, siteUrl: c.siteUrl.replace(/\/+$/, ""), email: c.kind === "jira" ? c.email : undefined, healthy: false, addedAt: new Date().toISOString() });
  writeTrackersFile(entries, workspace, home);
  return { id: c.id, kind: c.kind, siteUrl: c.siteUrl, email: c.email, workspace, home };
}

export async function removeSeededConnection(c: Pick<SeededConnection, "id" | "workspace" | "home">) {
  deleteSecret(c.workspace, c.home, c.id);
  writeTrackersFile(readTrackersFile(c.workspace, c.home).filter((e) => e.id !== c.id), c.workspace, c.home);
}
