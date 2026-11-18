import type { OrkkaApi } from "../api/client.js";
import type { MyRunnerResponse } from "../api/types.js";
import { pollUntil } from "../api/poll.js";

/**
 * Runner protocol v4: git with the developer's credentials is required for any git work, and
 * `command-trust` (the runner vets every setup/check/build/run-target command against orkka.json
 * on the default branch and the developer's approvals) — the host sends no repository command to
 * a runner without it. It replaced the old `setup-command` / `worktree-command` capabilities.
 */
export const HYBRID_BASE_CAPS = ["hybrid", "developer-git", "command-trust"] as const;
export const HYBRID_FOLDER_CAPS = [...HYBRID_BASE_CAPS, "local-folders"] as const;
/** Publishing a remote repo's reviewed work as a forge PR with gh/az/glab. */
export const FORGE_CAPS = [...HYBRID_BASE_CAPS, "forge-cli"] as const;
/** Ticket source settings (relayed queries) and polling. */
export const TRACKER_CAPS = [...FORGE_CAPS, "tracker-config", "tracker-poll"] as const;
export const APP_RUN_CAPS = ["app-run", "app-run-prepare"] as const;

export function isRunnerReady(r: MyRunnerResponse, caps: readonly string[] = HYBRID_BASE_CAPS): boolean {
  if (!r.online) return false;
  const have = r.capabilities ?? [];
  return caps.every((c) => have.includes(c));
}

export function missingCaps(r: MyRunnerResponse, caps: readonly string[]): string[] {
  const have = r.capabilities ?? [];
  return caps.filter((c) => !have.includes(c));
}

/** Some AI CLI is installed and signed in — otherwise tasks park WaitingForRunner. */
export function anyProviderReady(r: MyRunnerResponse): boolean {
  const providers = r.providers ?? r.lastKnownProviders ?? [];
  return providers.some((p) => p.cliFound && p.authenticated);
}

export function claudeReady(r: MyRunnerResponse): boolean {
  const providers = r.providers ?? r.lastKnownProviders ?? [];
  const p = providers.find((x) => x.provider === "claude");
  return Boolean(p && p.cliFound && p.authenticated);
}

export function waitForRunner(api: OrkkaApi, caps: readonly string[], deadlineMs: number): Promise<MyRunnerResponse> {
  return pollUntil({
    label: `runner online with [${caps.join(", ")}]`,
    deadlineMs,
    intervalMs: 5000,
    fetch: () => api.me.runner(),
    done: (r) => isRunnerReady(r, caps),
  });
}

export function waitForRunnerOffline(api: OrkkaApi, deadlineMs: number): Promise<MyRunnerResponse> {
  return pollUntil({ label: "runner offline", deadlineMs, intervalMs: 10_000, fetch: () => api.me.runner(), done: (r) => !r.online });
}
