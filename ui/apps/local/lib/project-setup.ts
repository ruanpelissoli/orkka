import { isProviderReady, readyProviders } from "@/lib/model-billing";
import type {
  ForgeCliName,
  MyRunnerStatus,
  Project,
  ProjectRepo,
  ProjectSetupStep,
  ProjectTrackerConfig,
  RepoAccessCheck,
  RepoCommandsCheck,
  WorkspaceAiProvider,
} from "@/lib/types";

/**
 * The guided project setup's readiness rules — one pure function, so every step's panel, the
 * Review checklist and Start's gating read the same verdicts (and vitest pins them).
 *
 * Green = ready; amber = it will work, but something is worth a look (Start allowed); red = the
 * first ticket would fail (Start disabled unless the developer chooses "Start anyway").
 */

/** Every step in order; "review" is finished by Start. */
export const SETUP_STEPS: ProjectSetupStep[] = ["folder", "machine", "source", "team", "review"];

/** The steps a developer confirms with "Continue". */
export const CONFIRMABLE_STEPS: ProjectSetupStep[] = ["folder", "machine", "source", "team"];

export type CheckLevel = "ok" | "warn" | "error";

export interface SetupCheck {
  /** Stable id (`machine.runner`, `folder.command.<repo>.<kind>`…) — tests and React keys. */
  id: string;
  /** Where it is fixed: the step the checklist links back to. */
  step: ProjectSetupStep;
  level: CheckLevel;
  /** i18n key under `dashboard.projectSetup.checks`. */
  label: string;
  params?: Record<string, string | number>;
  /** Extra words from the machine itself (a CLI's own detail, the access advice). */
  detail?: string | null;
  /** The exact command that fixes it, to copy. */
  fix?: string | null;
}

/** What the source step decided: a tracker kind, "none" (manual tasks only), or not yet. */
export type SourceChoice = "jira" | "ado" | "github" | "none" | null;

export interface SetupInputs {
  project: Pick<Project, "setupSteps" | "setupCompletedAt">;
  repos: ProjectRepo[];
  /** Null while it loads (no runner check is reported then). */
  runner: MyRunnerStatus | null;
  access: RepoAccessCheck | null;
  commands: RepoCommandsCheck | null;
  trackerConfig: ProjectTrackerConfig | null;
  aiProvider: WorkspaceAiProvider | null;
  /** The dry run ran (and answered) during this setup. */
  previewed: boolean;
}

/** The forge CLI that publishes a repository's pull requests, or null (folder / no CLI). */
export function forgeCliFor(repo: Pick<ProjectRepo, "provider" | "location">): ForgeCliName | null {
  if (repo.location === "local" || repo.provider === "local") return null;
  switch (repo.provider) {
    case "github":
      return "gh";
    case "azuredevops":
      return "az";
    case "gitlab":
      return "glab";
    default:
      return null;
  }
}

/** The command that makes a forge CLI ready. */
export const FORGE_CLI_FIX: Record<ForgeCliName, string> = {
  gh: "gh auth login",
  az: "az login",
  glab: "glab auth login",
};

/** The tracker kind the saved source uses, "none" when the developer chose manual tasks only,
 * null when the source step is still open. */
export function sourceChoiceOf(
  config: ProjectTrackerConfig | null,
  setupSteps: readonly string[] | null | undefined
): SourceChoice {
  if (config) {
    const kind = config.connection?.kind;
    return kind === "ado" ? "ado" : kind === "github" ? "github" : "jira";
  }
  return (setupSteps ?? []).includes("source") ? "none" : null;
}

export function folderChecks(input: Pick<SetupInputs, "repos" | "commands">): SetupCheck[] {
  const checks: SetupCheck[] = [];
  if (input.repos.length === 0) {
    checks.push({ id: "folder.repos", step: "folder", level: "error", label: "noRepos" });
    return checks;
  }
  checks.push({
    id: "folder.repos",
    step: "folder",
    level: "ok",
    label: "reposFound",
    params: { count: input.repos.length },
  });
  for (const repo of input.repos) {
    if (repo.missingSince) {
      checks.push({
        id: `folder.missing.${repo.id}`,
        step: "folder",
        level: "error",
        label: "repoMissing",
        params: { repo: repo.name },
      });
    }
  }

  if (input.commands?.supported) {
    for (const repo of input.commands.repos) {
      for (const command of repo.commands) {
        const id = `folder.command.${repo.repoId}.${command.kind}`;
        if (command.allowed) {
          checks.push({
            id,
            step: "folder",
            level: "ok",
            label: command.source === "approved" ? "commandApproved" : "commandDeclared",
            params: { repo: repo.name, kind: command.kind },
          });
        } else if (command.approvalId) {
          checks.push({
            id,
            step: "folder",
            level: "warn",
            label: "commandNeedsApproval",
            params: { repo: repo.name, kind: command.kind },
            detail: command.command,
            fix: `./orkka commands approve ${command.approvalId}`,
          });
        } else {
          checks.push({
            id,
            step: "folder",
            level: "warn",
            label: "commandUnchecked",
            params: { repo: repo.name, kind: command.kind },
            detail: command.error,
          });
        }
      }
    }
  }
  return checks;
}

export function machineChecks(input: Pick<SetupInputs, "repos" | "runner" | "access">): SetupCheck[] {
  const checks: SetupCheck[] = [];
  const runner = input.runner;
  if (!runner) return checks;

  if (!runner.online) {
    checks.push({ id: "machine.runner", step: "machine", level: "error", label: "runnerOffline", fix: "./orkka start" });
    return checks;
  }
  checks.push({
    id: "machine.runner",
    step: "machine",
    level: "ok",
    label: "runnerOnline",
    params: { machine: runner.machineName ?? "" },
  });

  const ready = readyProviders(runner.providers);
  checks.push(
    ready.length === 0
      ? { id: "machine.ai", step: "machine", level: "error", label: "noAiCli", fix: "claude" }
      : {
          id: "machine.ai",
          step: "machine",
          level: "ok",
          label: "aiReady",
          params: { providers: ready.map((p) => p.provider).join(", ") },
        }
  );

  const identity = runner.commitIdentity;
  if (identity) {
    checks.push(
      identity.source === "none"
        ? {
            id: "machine.identity",
            step: "machine",
            level: "error",
            label: "identityMissing",
            fix: './orkka workspace set-identity <workspace> --name "Your Name" --email you@example.com',
          }
        : {
            id: "machine.identity",
            step: "machine",
            level: "ok",
            label: identity.source === "workspace" ? "identityWorkspace" : "identityGitConfig",
            params: { name: identity.name ?? "", email: identity.email ?? "" },
          }
    );
  }

  if (input.access?.supported) {
    for (const status of input.access.repos) {
      checks.push({
        id: `machine.access.${status.repoId}`,
        step: "machine",
        level: status.ok ? "ok" : "error",
        label: status.ok ? "repoReachable" : "repoUnreachable",
        params: { repo: status.name },
        detail: status.ok ? null : status.advice,
      });
    }
  }

  const clis = runner.forgeClis;
  if (clis) {
    const needed = new Map<ForgeCliName, string[]>();
    for (const repo of input.repos) {
      const cli = forgeCliFor(repo);
      if (cli) needed.set(cli, [...(needed.get(cli) ?? []), repo.name]);
    }
    for (const [cli, repoNames] of needed) {
      const status = clis.find((c) => c.cli === cli);
      const ok = !!status?.found && status.authenticated;
      checks.push({
        id: `machine.forge.${cli}`,
        step: "machine",
        level: ok ? "ok" : "error",
        label: ok ? "forgeReady" : status?.found ? "forgeNotSignedIn" : "forgeMissing",
        params: { cli, repos: repoNames.join(", ") },
        detail: ok ? null : (status?.detail ?? null),
        fix: ok ? null : FORGE_CLI_FIX[cli],
      });
    }
  }
  return checks;
}

export function sourceChecks(
  input: Pick<SetupInputs, "project" | "trackerConfig" | "previewed">
): SetupCheck[] {
  const choice = sourceChoiceOf(input.trackerConfig, input.project.setupSteps);
  if (choice === null)
    return [{ id: "source.choice", step: "source", level: "error", label: "sourceNotChosen" }];
  if (choice === "none")
    return [{ id: "source.choice", step: "source", level: "ok", label: "sourceNone" }];

  const config = input.trackerConfig!;
  const checks: SetupCheck[] = [];
  const connection = config.connection;
  checks.push(
    !connection || !connection.healthy
      ? {
          id: "source.connection",
          step: "source",
          level: "error",
          label: "connectionUnhealthy",
          detail: connection?.healthDetail ?? null,
        }
      : {
          id: "source.connection",
          step: "source",
          level: "ok",
          label: "sourceSet",
          params: { scope: config.boardName ? `${config.scope} · ${config.boardName}` : config.scope },
        }
  );
  if (!config.enabled)
    checks.push({ id: "source.enabled", step: "source", level: "warn", label: "sourcePaused" });
  if (config.lastPollError)
    checks.push({
      id: "source.pollError",
      step: "source",
      level: "warn",
      label: "lastPollError",
      detail: config.lastPollError,
    });
  checks.push(
    input.previewed
      ? { id: "source.preview", step: "source", level: "ok", label: "previewed" }
      : { id: "source.preview", step: "source", level: "warn", label: "notPreviewed" }
  );
  return checks;
}

export function teamChecks(input: Pick<SetupInputs, "runner" | "aiProvider">): SetupCheck[] {
  const checks: SetupCheck[] = [];
  if (input.aiProvider?.blocker) {
    checks.push({
      id: "team.provider",
      step: "team",
      level: "warn",
      label: "providerBlocked",
      detail: input.aiProvider.blocker,
    });
  } else if (input.aiProvider) {
    checks.push({
      id: "team.provider",
      step: "team",
      level: "ok",
      label: "providerSet",
      params: { provider: input.aiProvider.effectiveProvider },
    });
  }
  const providers = input.runner?.online ? input.runner.providers : input.runner?.lastKnownProviders;
  if (providers) {
    const claude = providers.find((p) => p.provider === "claude");
    if (!claude || !isProviderReady(claude))
      checks.push({ id: "team.qa", step: "team", level: "warn", label: "qaNeedsClaude", fix: "claude" });
  }
  return checks;
}

/** Steps the developer has not confirmed yet — amber on the checklist. */
export function unconfirmedSteps(project: Pick<Project, "setupSteps">): ProjectSetupStep[] {
  const done = new Set(project.setupSteps ?? []);
  return CONFIRMABLE_STEPS.filter((s) => !done.has(s));
}

/** The Review checklist: every step's checks, then the steps never confirmed. */
export function setupChecklist(input: SetupInputs): SetupCheck[] {
  return [
    ...folderChecks(input),
    ...machineChecks(input),
    ...sourceChecks(input),
    ...teamChecks(input),
    ...unconfirmedSteps(input.project).map(
      (step): SetupCheck => ({
        id: `step.${step}`,
        step,
        level: "warn",
        label: "stepNotConfirmed",
        params: { step },
      })
    ),
  ];
}

/** The worst level among the checks of one step (for the stepper's dot). */
export function stepLevel(checks: readonly SetupCheck[], step: ProjectSetupStep): CheckLevel | null {
  const own = checks.filter((c) => c.step === step);
  if (own.length === 0) return null;
  if (own.some((c) => c.level === "error")) return "error";
  if (own.some((c) => c.level === "warn")) return "warn";
  return "ok";
}

/** Start is allowed outright only when nothing is red. */
export function canStart(checks: readonly SetupCheck[]): boolean {
  return !checks.some((c) => c.level === "error");
}

/** Where the setup page resumes: the first step not confirmed, else Review. */
export function resumeStep(project: Pick<Project, "setupSteps">): ProjectSetupStep {
  return unconfirmedSteps(project)[0] ?? "review";
}

/** `?step=` as a known step, else null. */
export function parseStep(value: string | null | undefined): ProjectSetupStep | null {
  return SETUP_STEPS.find((s) => s === value) ?? null;
}
