/**
 * Command trust on the harness's runner (`./orkka commands`). The runner runs a repository's
 * setup / check / build command or a run target's start/install/stop command only when:
 *   1. the repository's orkka.json on its DEFAULT branch declares it (the fixtures under
 *      fixtures/repos declare theirs, so the bare remotes seeded from them are trusted), or
 *   2. the developer approved it on the machine: `./orkka commands approve <id>`.
 * Anything else is refused, recorded as pending in `<ORKKA_HOME>/command-approvals.json`, and the
 * orchestrator posts a system note on the task thread naming the id.
 *
 * Everything here runs in the SANDBOX (`runnerCli` → ORKKA_HOME=E2E_RUNNER_HOME): approvals land in
 * the harness's own Orkka home, never the developer's ~/.orkka.
 */
import fs from "node:fs";
import path from "node:path";
import { runnerCli } from "./process.js";

/** `./orkka commands approve <id>` — the id is the first 8 hex chars of sha256(repo + command). */
const APPROVE_ID = /\.\/orkka commands approve ([0-9a-f]{8})/g;

/** Ids of commands a task-thread note (or any text) asks the developer to approve. */
export function approvalIdsIn(text: string): string[] {
  return [...new Set([...text.matchAll(APPROVE_ID)].map((m) => m[1]))];
}

export interface CommandListing {
  exitCode: number;
  stdout: string;
  pending: string[];
  approved: string[];
}

/** `./orkka commands` in the sandbox, parsed into its pending and approved ids. */
export async function listRepoCommands(extraEnv: Record<string, string> = {}): Promise<CommandListing> {
  const r = await runnerCli(["commands"], { extraEnv });
  return { exitCode: r.exitCode ?? -1, stdout: r.stdout, ...parseCommandListing(r.stdout) };
}

/** Split `./orkka commands` output into the "Waiting for your approval:" and "Approved on this machine:" ids. */
export function parseCommandListing(stdout: string): { pending: string[]; approved: string[] } {
  const pending: string[] = [];
  const approved: string[] = [];
  let section: "pending" | "approved" | null = null;
  for (const line of stdout.split(/\r?\n/)) {
    if (/^Waiting for your approval:/.test(line)) section = "pending";
    else if (/^Approved on this machine:/.test(line)) section = "approved";
    else if (section) {
      const m = /^ {2}([0-9a-f]{8}) /.exec(line);
      if (m) (section === "pending" ? pending : approved).push(m[1]);
    }
  }
  return { pending, approved };
}

/** `./orkka commands approve --yes <id>` in the sandbox (no prompt). */
export async function approveRepoCommand(id: string, extraEnv: Record<string, string> = {}) {
  const r = await runnerCli(["commands", "approve", "--yes", id], { extraEnv });
  if (r.exitCode !== 0) throw new Error(`./orkka commands approve ${id} failed (${r.exitCode}): ${r.stdout}${r.stderr}`);
  return r;
}

/** `./orkka commands revoke <id>` in the sandbox — cleanup for approvals a spec made. */
export async function revokeRepoCommand(id: string, extraEnv: Record<string, string> = {}) {
  return runnerCli(["commands", "revoke", id], { extraEnv });
}

/** The CommandKind enum as the runner serializes it in command-approvals.json. */
export const COMMAND_KIND = { setup: 0, check: 1, build: 2, runTarget: 3 } as const;

/**
 * Seed a PENDING entry straight into an Orkka home's command-approvals.json — what the runner
 * writes when it refuses a command. Only for the offline CLI spec, which has no runner daemon to
 * produce a real refusal. The file format is the runner's (PascalCase, numeric kind).
 */
export function seedPendingCommand(home: string, entry: { id: string; repo: string; command: string; kind: keyof typeof COMMAND_KIND; workspace: string }) {
  const file = path.join(home, "command-approvals.json");
  const model = fs.existsSync(file) ? (JSON.parse(fs.readFileSync(file, "utf8")) as { Approved: unknown[]; Pending: unknown[] }) : { Approved: [], Pending: [] };
  model.Pending.push({
    Id: entry.id,
    Repo: entry.repo,
    Command: entry.command,
    Kind: COMMAND_KIND[entry.kind],
    Workspace: entry.workspace,
    FirstSeenAt: new Date().toISOString(),
    ApprovedAt: null,
  });
  fs.mkdirSync(home, { recursive: true });
  fs.writeFileSync(file, JSON.stringify(model, null, 2));
}
