import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import dotenv from "dotenv";
import { z } from "zod";

const here = path.dirname(fileURLToPath(import.meta.url));
export const repoRoot = path.resolve(here, "..");
/** The orkka monorepo root (e2e/ lives inside it). */
export const monoRoot = path.resolve(repoRoot, "..");
dotenv.config({ path: path.join(repoRoot, ".env"), quiet: true });

const boolish = z
  .string()
  .optional()
  .transform((v) => v === "1" || v === "true");
const optionalUrl = z
  .string()
  .optional()
  .transform((v) => (v && v.trim() ? v.trim().replace(/\/+$/, "") : undefined));

const home = os.homedir();

/** The app `./orkka start` runs (built by `./orkka` / `orkka.cmd`, or `dotnet build src/Orkka -c Release`). */
function defaultOrkkaDll() {
  return path.join(process.env.E2E_SRC_API?.trim() || monoRoot, "src", "Orkka", "bin", "Release", "net10.0", "orkka.dll");
}

/**
 * Everything is configured through `.env` (see `.env.example`). There is NO default environment:
 * with `E2E_BASE_URL` unset the harness runs only the offline specs (fake tracker, stub gh, bare
 * remotes) and every spec that needs a deployed Orkka skips itself.
 */
const EnvSchema = z.object({
  /** The sandboxed Orkka, http://127.0.0.1:<port> — `stack:start` runs `start --port <port>` from it. Unset ⇒ offline only. */
  E2E_BASE_URL: optionalUrl,
  /** The API — the same origin as E2E_BASE_URL. */
  E2E_API_URL: optionalUrl,
  /** The workspace profile the offline runner-CLI specs use (never one of your real ones). */
  E2E_RUNNER_WORKSPACE: z.string().regex(/^[a-z0-9][a-z0-9-]{0,40}$/).default("e2e"),
  /** `orkka.dll`, run as `dotnet orkka.dll …` (what `./orkka` runs). Default: this clone's Release build. */
  E2E_ORKKA_DLL: z.string().default(defaultOrkkaDll()),
  /** ORKKA_HOME of the offline runner-CLI specs. Never your real ~/.orkka. */
  E2E_RUNNER_HOME: z.string().default(path.join(repoRoot, ".runs", "orkka-home")),
  /** The local-stack sandbox: `<dir>/home` is its ORKKA_HOME, `<dir>/screens` the screenshots. Never your real ~/.orkka. */
  E2E_LOCAL_SANDBOX: z.string().default(path.join(repoRoot, ".runs", "local-stack")),
  /** 1 = `./orkka stop` the sandboxed Orkka when the run ends. */
  E2E_STACK_STOP_AFTER: boolish,
  /** Refuse *.orkka.ai unless set — prod was torn down and must never be the default target. */
  E2E_ALLOW_PROD: boolish,
  /** Fixed port for the fake Jira/ADO server (0 = any free port). */
  E2E_FAKE_TRACKER_PORT: z.coerce.number().int().min(0).max(65535).default(0),
  /** Where bare remotes, local folders and stub CLIs are materialised per run. */
  E2E_SCRATCH_DIR: z.string().default(path.join(repoRoot, ".runs", "scratch")),
  /** Local-folder fixtures (the runner must be able to read it). */
  E2E_LOCAL_FIXTURE_PARENT: z.string().default(path.join(home, "orkka-e2e-local")),
  E2E_SRC_UI: z.string().default(path.resolve(monoRoot, "ui")),
  E2E_SRC_API: z.string().default(monoRoot),
  E2E_SUITE: z.string().optional(),
});

// `KEY=` in .env means "unset", so defaults apply.
const raw = Object.fromEntries(Object.entries(process.env).filter(([k, v]) => !(k.startsWith("E2E_") && (v ?? "").trim() === "")));
const parsed = EnvSchema.safeParse(raw);
if (!parsed.success) {
  const issues = parsed.error.issues.map((i) => `  ${i.path.join(".")}: ${i.message}`).join("\n");
  throw new Error(`Invalid .env / environment for orkka-e2e-tests:\n${issues}`);
}
export const env = parsed.data;

function isProdHost(url: string | undefined) {
  if (!url) return false;
  try {
    return /(^|\.)orkka\.(ai|app)$/i.test(new URL(url).hostname);
  } catch {
    return false;
  }
}
for (const [k, v] of [["E2E_BASE_URL", env.E2E_BASE_URL], ["E2E_API_URL", env.E2E_API_URL]] as const) {
  if (isProdHost(v) && !env.E2E_ALLOW_PROD) {
    throw new Error(`${k}=${v} points at production. Prod is torn down and is never a default target; set E2E_ALLOW_PROD=1 only if the user explicitly asked.`);
  }
}

export interface TargetConfig {
  /** True when a sandboxed Orkka is configured (E2E_BASE_URL). */
  configured: boolean;
  webUrl: string;
  apiUrl: string;
  hubs: { tasks: string };
  rateLimit: { generalPerMin: number; aiPerMin: number };
  timeouts: { aiPhaseMs: number; runnerOnlineMs: number; pipelineTestMs: number; trackerPollMs: number };
  projectPrefix: string;
  projectKey: string;
}

const configured = Boolean(env.E2E_BASE_URL);
export const cfg: TargetConfig = {
  configured,
  // Placeholder origin when offline so URL construction never throws; nothing calls it then.
  webUrl: env.E2E_BASE_URL ?? "http://127.0.0.1:9",
  apiUrl: env.E2E_API_URL ?? env.E2E_BASE_URL ?? "http://127.0.0.1:9",
  hubs: { tasks: "/hubs/tasks" },
  rateLimit: { generalPerMin: 280, aiPerMin: 25 },
  timeouts: {
    aiPhaseMs: 20 * 60_000,
    runnerOnlineMs: 120_000,
    pipelineTestMs: 45 * 60_000,
    // Poll interval floor is 60 s and the runner syncs every 30 s; allow a few rounds.
    trackerPollMs: 4 * 60_000,
  },
  projectPrefix: "e2e-",
  projectKey: "E2E",
};

/** Why an environment-bound spec is skipped, or undefined when it can run. */
export function needsEnvironment(_opts: { runner?: boolean } = {}): string | undefined {
  if (!cfg.configured) return "no local stack configured (set E2E_BASE_URL=http://127.0.0.1:<port> in .env and run `npm run stack:start`)";
  return undefined;
}

export const paths = {
  root: repoRoot,
  auth: path.join(repoRoot, ".auth"),
  storageState: path.join(repoRoot, ".auth", "storageState.json"),
  apiKey: path.join(repoRoot, ".auth", "api-key.json"),
  org: path.join(repoRoot, ".auth", "org.json"),
  runsDir: path.join(repoRoot, "runs"),
  scratch: path.join(repoRoot, ".runs"),
  current: path.join(repoRoot, ".runs", "current"),
  catalog: path.join(repoRoot, "catalog"),
  fixtures: path.join(repoRoot, "fixtures", "repos"),
  stubGhSource: path.join(repoRoot, "fixtures", "stub-gh"),
};

export const ALL_PERMISSIONS = [
  "TasksCreate",
  "TasksEdit",
  "TasksDelete",
  "TasksExecute",
  "PlansReview",
  "CodeReviewReview",
  "CodeReviewMerge",
  "DocumentsManage",
  "AgentsChat",
  "AgentsConfigure",
  "DesignReview",
  "ProjectsCreate",
  "ProjectsManage",
  "ProjectsDelete",
  "ReposManage",
  "BillingManage",
  "AuditView",
] as const;
export type Permission = (typeof ALL_PERMISSIONS)[number];
