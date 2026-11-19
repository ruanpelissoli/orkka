import { describe, expect, it } from "vitest";
import {
  canStart,
  forgeCliFor,
  machineChecks,
  parseStep,
  resumeStep,
  setupChecklist,
  sourceChoiceOf,
  stepLevel,
  type SetupInputs,
} from "@/lib/project-setup";
import type { MyRunnerStatus, ProjectRepo, ProjectTrackerConfig } from "@/lib/types";

function repo(overrides: Partial<ProjectRepo> = {}): ProjectRepo {
  return {
    id: "r1",
    projectId: "p1",
    name: "api",
    gitUrl: "https://github.com/acme/api.git",
    defaultBranch: "main",
    provider: "github",
    workspaceStatus: "Ready",
    cardColor: "#fff",
    setupCommand: null,
    qaRunCommand: null,
    buildCommand: null,
    checkCommand: null,
    createdAt: "2026-11-19T00:00:00Z",
    location: "remote",
    ...overrides,
  } as ProjectRepo;
}

function runner(overrides: Partial<MyRunnerStatus> = {}): MyRunnerStatus {
  return {
    online: true,
    machineName: "dana-laptop",
    daemonVersion: "1.0",
    connectedAt: null,
    lastSeenAt: null,
    providers: [{ provider: "claude", cliFound: true, authenticated: true, models: null, detail: null }],
    lastKnownProviders: null,
    lastKnownAt: null,
    lastKnownMachineName: null,
    hasActiveTokens: true,
    forgeClis: [{ cli: "gh", found: true, authenticated: true, accounts: ["dana"], detail: null }],
    commitIdentity: { name: "Dana", email: "dana@acme.com", source: "git-config" },
    ...overrides,
  } as MyRunnerStatus;
}

function config(overrides: Partial<ProjectTrackerConfig> = {}): ProjectTrackerConfig {
  return {
    id: "c1",
    projectId: "p1",
    ownerUserId: "local_user",
    trackerConnectionId: "conn1",
    connection: {
      id: "conn1",
      key: "jira-acme",
      kind: "jira",
      siteUrl: "https://acme.atlassian.net",
      accountId: null,
      displayName: "Dana",
      healthy: true,
      healthDetail: null,
      machineName: "dana-laptop",
      lastSeenAt: "2026-11-19T00:00:00Z",
      checkedAt: null,
    },
    scope: "SHOP",
    enabled: true,
    lastPollError: null,
    ...overrides,
  } as ProjectTrackerConfig;
}

function inputs(overrides: Partial<SetupInputs> = {}): SetupInputs {
  return {
    project: { setupSteps: ["folder", "machine", "source", "team"], setupCompletedAt: null },
    repos: [repo()],
    runner: runner(),
    access: { supported: true, repos: [{ repoId: "r1", name: "api", ok: true, reason: null, advice: null }] },
    commands: { supported: true, repos: [{ repoId: "r1", name: "api", commands: [] }] },
    trackerConfig: config(),
    aiProvider: { provider: "automatic", choices: ["automatic"], effectiveProvider: "claude", blocker: null },
    previewed: true,
    ...overrides,
  };
}

const levels = (checks: { id: string; level: string }[]) =>
  Object.fromEntries(checks.map((c) => [c.id, c.level]));

describe("project setup checklist", () => {
  it("is all green when everything is ready, and Start is allowed", () => {
    const checks = setupChecklist(inputs());
    expect(checks.every((c) => c.level === "ok")).toBe(true);
    expect(canStart(checks)).toBe(true);
  });

  it("an offline runner is red, with the command that starts it", () => {
    const checks = machineChecks({ repos: [repo()], runner: runner({ online: false }), access: null });
    expect(checks).toEqual([expect.objectContaining({ id: "machine.runner", level: "error", fix: "./orkka start" })]);
    expect(canStart(setupChecklist(inputs({ runner: runner({ online: false }) })))).toBe(false);
  });

  it("no signed-in AI CLI, a missing commit identity and an unreachable repo are red", () => {
    const checks = setupChecklist(
      inputs({
        runner: runner({
          providers: [{ provider: "claude", cliFound: true, authenticated: false, models: null, detail: null }],
          commitIdentity: { name: null, email: null, source: "none" },
        }),
        access: {
          supported: true,
          repos: [{ repoId: "r1", name: "api", ok: false, reason: "AuthFailed", advice: "Sign git in." }],
        },
      })
    );
    const byId = levels(checks);
    expect(byId["machine.ai"]).toBe("error");
    expect(byId["machine.identity"]).toBe("error");
    expect(byId["machine.access.r1"]).toBe("error");
    expect(checks.find((c) => c.id === "machine.access.r1")?.detail).toBe("Sign git in.");
    // QA needs Claude: amber, not red.
    expect(byId["team.qa"]).toBe("warn");
  });

  it("asks for the forge CLI only for repositories that publish pull requests", () => {
    const missingGh = runner({ forgeClis: [{ cli: "gh", found: false, authenticated: false, accounts: null, detail: null }] });
    expect(levels(machineChecks({ repos: [repo()], runner: missingGh, access: null }))["machine.forge.gh"]).toBe("error");
    expect(
      levels(machineChecks({ repos: [repo({ provider: "local", location: "local" })], runner: missingGh, access: null }))[
        "machine.forge.gh"
      ]
    ).toBeUndefined();
    expect(forgeCliFor(repo({ provider: "azuredevops" }))).toBe("az");
    expect(forgeCliFor(repo({ provider: "bitbucket" }))).toBeNull();
  });

  it("a command waiting for approval is amber, with the exact approve command", () => {
    const checks = setupChecklist(
      inputs({
        commands: {
          supported: true,
          repos: [
            {
              repoId: "r1",
              name: "api",
              commands: [
                { kind: "setup", command: "npm ci", allowed: true, source: "declared", approvalId: null, machine: null, error: null },
                { kind: "check", command: "npm test", allowed: false, source: null, approvalId: "ab12cd34", machine: "dana-laptop", error: null },
              ],
            },
          ],
        },
      })
    );
    const pending = checks.find((c) => c.id === "folder.command.r1.check")!;
    expect(pending.level).toBe("warn");
    expect(pending.fix).toBe("./orkka commands approve ab12cd34");
    expect(checks.find((c) => c.id === "folder.command.r1.setup")?.label).toBe("commandDeclared");
    expect(canStart(checks)).toBe(true);
  });

  it("the source: not chosen is red, none is green, a source not previewed is amber", () => {
    expect(levels(setupChecklist(inputs({ trackerConfig: null, project: { setupSteps: [], setupCompletedAt: null } })))["source.choice"]).toBe(
      "error"
    );
    expect(levels(setupChecklist(inputs({ trackerConfig: null })))["source.choice"]).toBe("ok");
    expect(levels(setupChecklist(inputs({ previewed: false })))["source.preview"]).toBe("warn");
    expect(
      levels(setupChecklist(inputs({ trackerConfig: config({ connection: { ...config().connection!, healthy: false } }) })))[
        "source.connection"
      ]
    ).toBe("error");
  });

  it("an unconfirmed step is amber and the setup resumes there", () => {
    const project = { setupSteps: ["folder" as const], setupCompletedAt: null };
    const checks = setupChecklist(inputs({ project }));
    expect(checks.filter((c) => c.label === "stepNotConfirmed").map((c) => c.step)).toEqual(["machine", "source", "team"]);
    expect(resumeStep(project)).toBe("machine");
    expect(resumeStep({ setupSteps: ["folder", "machine", "source", "team"] })).toBe("review");
    expect(resumeStep({ setupSteps: null })).toBe("folder");
  });

  it("each step shows its worst level", () => {
    const checks = setupChecklist(inputs({ runner: runner({ online: false }), previewed: false }));
    expect(stepLevel(checks, "machine")).toBe("error");
    expect(stepLevel(checks, "source")).toBe("warn");
    expect(stepLevel(checks, "folder")).toBe("ok");
  });

  it("reads the source choice and the step from the URL", () => {
    expect(sourceChoiceOf(config(), [])).toBe("jira");
    expect(sourceChoiceOf(config({ connection: { ...config().connection!, kind: "github" } }), [])).toBe("github");
    expect(sourceChoiceOf(null, ["source"])).toBe("none");
    expect(sourceChoiceOf(null, [])).toBeNull();
    expect(parseStep("team")).toBe("team");
    expect(parseStep("nope")).toBeNull();
  });
});
