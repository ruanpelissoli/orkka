/**
 * `./orkka` CLI calls for the OFFLINE specs (`tracker add/test/list/remove`, `commands …`) in a
 * SANDBOX — `ORKKA_HOME` points at `E2E_RUNNER_HOME` (never the developer's ~/.orkka), the
 * workspace profile is `E2E_RUNNER_WORKSPACE`, PATH starts with the stub-CLI bin dir, and git
 * resolves the `https://github.com/e2e-local/*` aliases to local bare remotes. They run
 * `dotnet orkka.dll …`, exactly what the `./orkka` script runs. The running Orkka's own runner
 * lives in its process instead — see src/local/stack.ts.
 */
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execa } from "execa";
import { env } from "../../config/index.js";
import { aliasGitEnv } from "../testdata/bare-remote.js";

export function stubBinDir() {
  return path.join(env.E2E_SCRATCH_DIR, "bin");
}

/** The built `orkka.dll`, or undefined when this clone was never built (CLI specs then skip). */
export function orkkaDllIfBuilt(): string | undefined {
  return fs.existsSync(env.E2E_ORKKA_DLL) ? env.E2E_ORKKA_DLL : undefined;
}

/**
 * The runner's test opt-ins: a fake tracker on loopback is kept as typed (http + port, loopback
 * only), and secrets — tracker tokens AND the runner token, which normally lives in the OS
 * secret store — stay in `<ORKKA_HOME>/workspaces/<ws>/secrets/`, never the OS keychain. (A
 * non-default ORKKA_HOME already forces the file store; the variable makes it explicit.)
 */
export const RUNNER_TEST_OPT_INS = {
  ORKKA_TRACKER_ALLOW_INSECURE_SITE: "1",
  ORKKA_SECRET_STORE: "file",
} as const;

/** The environment `./orkka tracker …` CLI calls run with. */
export function sandboxEnv(extra: Record<string, string> = {}): Record<string, string> {
  const base = { ...process.env } as Record<string, string>;
  const pathKey = Object.keys(base).find((k) => k.toLowerCase() === "path") ?? "PATH";
  return {
    ...base,
    [pathKey]: `${stubBinDir()}${path.delimiter}${base[pathKey] ?? ""}`,
    ORKKA_HOME: env.E2E_RUNNER_HOME,
    ORKKA_NO_UPDATE_CHECK: "1",
    ...RUNNER_TEST_OPT_INS,
    ...aliasGitEnv(),
    ...extra,
  };
}

/** `dotnet orkka.dll <args>` with the given environment; never prompts. */
export function orkkaCli(args: string[], opts: { env: Record<string, string>; input?: string; timeoutMs?: number }) {
  return execa("dotnet", [env.E2E_ORKKA_DLL, ...args], {
    env: opts.env,
    input: opts.input ?? "",
    reject: false,
    timeout: opts.timeoutMs ?? 90_000,
    cwd: os.homedir(),
    windowsHide: true,
  });
}

/** Run an `./orkka` CLI command (e.g. `tracker list`) in the sandbox; never prompts. */
export async function runnerCli(args: string[], opts: { input?: string; extraEnv?: Record<string, string>; timeoutMs?: number } = {}) {
  return orkkaCli(args, { env: sandboxEnv(opts.extraEnv), input: opts.input, timeoutMs: opts.timeoutMs });
}
