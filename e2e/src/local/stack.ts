/**
 * The Orkka under test: `orkka.dll start --no-browser --port <port>` (exactly what `./orkka start`
 * runs after its build step) in a SANDBOX Orkka home — never the developer's ~/.orkka. Since R4 it
 * is ONE process: the host (API, engine, hubs, the built static UI, SQLite at
 * `<home>/data/orkka.db`) and the runner, in the foreground. The harness starts it as a detached
 * background child, waits for `/health` (`service: orkka`), signs a browser in with the URL
 * `open --print` prints, and stops it with `stop` (killing the process tree when that fails).
 *
 * There is no account: the install secret (a file in the sandbox's file secret store) is the root
 * credential, an `orkka_key_` API key drives the REST API and a one-time launch link gives a
 * browser its session cookie. The process's environment is inherited by the in-process runner, so
 * the stub `gh` (first on PATH), the git alias for the local bare remotes and the runner's test
 * opt-ins are all set here.
 */
import { spawn } from "node:child_process";
import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import { execa } from "execa";
import { chromium } from "@playwright/test";
import { ALL_PERMISSIONS, env, paths } from "../../config/index.js";
import { aliasGitEnv } from "../testdata/bare-remote.js";
import { ensureDir, readJson, stamp, writeJson } from "../util/fs.js";
import { log } from "../util/log.js";

/** The port the sandboxed Orkka listens on: E2E_BASE_URL's (pick one nothing else uses). */
export function stackPort(): number {
  const url = env.E2E_BASE_URL;
  const port = url ? Number(new URL(url).port || 80) : 6750;
  return port;
}

export function sandboxDir() {
  return env.E2E_LOCAL_SANDBOX;
}
export function orkkaHome() {
  return path.join(sandboxDir(), "home");
}
export function stubBinDir() {
  return path.join(sandboxDir(), "bin", "stub");
}
export function localSecretsDir() {
  return path.join(orkkaHome(), "workspaces", "_local", "secrets");
}
export function logsDir() {
  return path.join(orkkaHome(), "logs");
}
/** The SQLite database (plus its -wal/-shm siblings). */
export function databaseFile() {
  return path.join(orkkaHome(), "data", "orkka.db");
}
/** What `start` printed to its terminal (the structured log is `<home>/logs/orkka.log`). */
export function consoleLog() {
  return path.join(sandboxDir(), "orkka-start.out");
}
function instanceFile() {
  return path.join(orkkaHome(), "run", "instance.json");
}

/** `orkka.dll`, checked: the stack and the CLI specs run `dotnet <dll> …`. */
export function orkkaDll(): string {
  const dll = env.E2E_ORKKA_DLL;
  if (!fs.existsSync(dll)) throw new Error(`No Orkka build at ${dll} — run \`./orkka start\` once (or \`dotnet build src/Orkka -c Release\` + \`npm run build\` in ui/)`);
  return dll;
}

/** The process environment: inherited by the host and the in-process runner. */
export function stackEnv(extra: Record<string, string> = {}): Record<string, string> {
  const base = { ...process.env } as Record<string, string>;
  const pathKey = Object.keys(base).find((k) => k.toLowerCase() === "path") ?? "PATH";
  return {
    ...base,
    [pathKey]: `${stubBinDir()}${path.delimiter}${base[pathKey] ?? ""}`,
    ORKKA_HOME: orkkaHome(),
    ORKKA_SECRET_STORE: "file",
    ORKKA_NO_UPDATE_CHECK: "1",
    // The fake Jira/ADO listens on http://127.0.0.1:<port> — loopback only, kept as typed.
    ORKKA_TRACKER_ALLOW_INSECURE_SITE: "1",
    ...aliasGitEnv(),
    ...extra,
  };
}

/** A one-shot `dotnet orkka.dll <args>` against the sandbox (status, stop, open --print, …). */
export async function orkka(args: string[], opts: { timeoutMs?: number } = {}) {
  return execa("dotnet", [orkkaDll(), ...args], { env: stackEnv(), reject: false, timeout: opts.timeoutMs ?? 120_000, cwd: sandboxDir(), windowsHide: true });
}

export function portFree(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const s = net.createServer();
    s.once("error", () => resolve(false));
    s.listen(port, "127.0.0.1", () => s.close(() => resolve(true)));
  });
}

/** The stack's port when something listens on it. */
export async function listeners(): Promise<number[]> {
  const p = stackPort();
  return (await portFree(p)) ? [] : [p];
}

export function installSecret(): string {
  const f = path.join(localSecretsDir(), "install-secret");
  if (!fs.existsSync(f)) throw new Error(`No install secret at ${f} — is the sandboxed Orkka started?`);
  return fs.readFileSync(f, "utf8").trim();
}

interface InstanceInfo {
  pid?: number;
  Pid?: number;
  port?: number;
  Port?: number;
}
/** The running instance's pid/port from `<home>/run/instance.json`, if any. */
export function instanceInfo(): { pid: number; port: number } | undefined {
  const i = readJson<InstanceInfo>(instanceFile());
  if (!i) return undefined;
  const pid = i.pid ?? i.Pid;
  const port = i.port ?? i.Port;
  return pid && port ? { pid, port } : undefined;
}

export function hostUrl() {
  return `http://127.0.0.1:${instanceInfo()?.port ?? stackPort()}`;
}

export function isPidAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

/** Kill a process and everything it started (Windows: taskkill /T; else the process group). */
export async function killTree(pid: number) {
  if (!isPidAlive(pid)) return;
  if (process.platform === "win32") await execa("taskkill", ["/PID", String(pid), "/T", "/F"], { reject: false, windowsHide: true });
  else {
    try {
      process.kill(-pid, "SIGKILL");
    } catch {
      try {
        process.kill(pid, "SIGKILL");
      } catch {
        /* gone */
      }
    }
  }
}

async function waitHealthy(url: string, deadlineMs: number, childPid?: number) {
  const until = Date.now() + deadlineMs;
  let last = "";
  while (Date.now() < until) {
    if (childPid && !isPidAlive(childPid)) throw new Error(`orkka start exited before it was healthy — see ${consoleLog()} and ${logsDir()}`);
    try {
      const r = await fetch(`${url}/health`);
      const body = (await r.json().catch(() => ({}))) as { service?: string };
      if (r.ok && body.service === "orkka") return;
      last = `HTTP ${r.status} service=${body.service}`;
    } catch (e) {
      last = (e as Error).message;
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error(`Orkka at ${url} never became healthy (${last}); see ${consoleLog()} and ${logsDir()}`);
}

/**
 * Start the sandboxed Orkka in the background (`start --no-browser --port <port>`). `fresh` wipes
 * the sandbox's Orkka home first (database, repos, secrets). Returns the host URL.
 */
export async function startStack(opts: { fresh?: boolean } = {}): Promise<string> {
  ensureDir(sandboxDir());
  if (opts.fresh) {
    await stopStack();
    fs.rmSync(orkkaHome(), { recursive: true, force: true });
  }
  ensureDir(orkkaHome());
  ensureDir(stubBinDir());
  const dll = orkkaDll();
  // The stub gh must be on disk before the runner probes the forge CLIs in its first Hello.
  const { StubGh } = await import("../fakes/stub-gh.js");
  await StubGh.install(stubBinDir(), {}, { keepState: !opts.fresh });
  const port = stackPort();
  if (!(await portFree(port))) throw new Error(`port ${port} is already in use — another Orkka is running there; stop it or pick another port in E2E_BASE_URL`);

  log.step(`dotnet orkka.dll start --no-browser --port ${port} (ORKKA_HOME=${orkkaHome()})`);
  const out = fs.openSync(consoleLog(), "a");
  const child = spawn("dotnet", [dll, "start", "--no-browser", "--port", String(port)], {
    env: stackEnv(),
    cwd: sandboxDir(),
    detached: true,
    windowsHide: true,
    stdio: ["ignore", out, out],
  });
  fs.closeSync(out);
  child.unref();
  if (!child.pid) throw new Error("could not spawn dotnet");
  const url = `http://127.0.0.1:${port}`;
  try {
    await waitHealthy(url, 180_000, child.pid);
  } catch (e) {
    await killTree(child.pid);
    throw e;
  }
  log.info(`Orkka pid ${child.pid} healthy at ${url}`);
  return url;
}

/** `stop` (the runner drains, then the host); kills the process tree when that fails. */
export async function stopStack(): Promise<void> {
  if (!fs.existsSync(orkkaHome()) || !fs.existsSync(env.E2E_ORKKA_DLL)) return;
  const before = instanceInfo();
  const r = await orkka(["stop"], { timeoutMs: 120_000 });
  log.info(`orkka stop → ${r.exitCode}: ${(r.stdout || r.stderr).trim().split(/\r?\n/).slice(-3).join(" | ")}`);
  if (before && isPidAlive(before.pid)) {
    log.warn(`pid ${before.pid} still alive after stop — killing its tree`);
    await killTree(before.pid);
  }
}

export async function stackStatus(): Promise<string> {
  const r = await orkka(["status"]);
  return (r.stdout || r.stderr).trim();
}

export interface LocalAuth {
  baseUrl: string;
  apiKey: string;
  keyId: string;
  name: string;
  createdAt: string;
}

/** Mint an `orkka_key_` API key with every permission (Bearer install secret). */
export async function mintApiKey(baseUrl = hostUrl()): Promise<LocalAuth> {
  const secret = installSecret();
  const name = `e2e-harness-${stamp()}`;
  const r = await fetch(`${baseUrl}/api/me/api-keys`, {
    method: "POST",
    headers: { Authorization: `Bearer ${secret}`, "Content-Type": "application/json" },
    body: JSON.stringify({ name, permissions: [...ALL_PERMISSIONS] }),
  });
  if (!r.ok) throw new Error(`POST /api/me/api-keys → ${r.status}: ${await r.text()}`);
  const body = (await r.json()) as { key: { id: string }; rawKey: string };
  const saved: LocalAuth = { baseUrl, apiKey: body.rawKey, keyId: body.key.id, name, createdAt: new Date().toISOString() };
  writeJson(paths.apiKey, { key: saved.apiKey, keyId: saved.keyId, name, createdAt: saved.createdAt, permissions: [...ALL_PERMISSIONS] });
  log.info(`Minted API key ${name} → ${paths.apiKey}`);
  return saved;
}

/** A one-time sign-in link, exactly as `./orkka open --print` prints it. */
export async function launchUrl(): Promise<string> {
  const r = await orkka(["open", "--print"], { timeoutMs: 60_000 });
  const url = r.stdout.trim().split(/\r?\n/).pop() ?? "";
  if (r.exitCode !== 0 || !/^http:\/\/127\.0\.0\.1:\d+\/launch\?code=/.test(url)) throw new Error(`orkka open --print → ${r.exitCode}: ${r.stdout}${r.stderr}`);
  return url;
}

/** Follow the `open --print` link in a headless browser and save the session cookie as storageState. */
export async function browserSession(): Promise<string> {
  const url = await launchUrl();
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ locale: "en-US" });
    const page = await context.newPage();
    const res = await page.goto(url, { waitUntil: "domcontentloaded" });
    if (!res || res.status() >= 400) throw new Error(`/launch answered ${res?.status()}`);
    ensureDir(paths.auth);
    await context.storageState({ path: paths.storageState });
    log.info(`Browser session saved → ${paths.storageState} (landed on ${page.url()})`);
    return paths.storageState;
  } finally {
    await browser.close();
  }
}
