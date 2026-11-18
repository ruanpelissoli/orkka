import { test as base, expect, type Page } from "@playwright/test";
import { cfg, env } from "../../config/index.js";
import { OrkkaApi } from "../api/client.js";
import { pollUntil } from "../api/poll.js";
import type { ProjectResponse, ProjectSettings } from "../api/types.js";
import { createApi } from "../auth/api-key.js";
import { StubGh } from "../fakes/stub-gh.js";
import { FakeTrackerServer, type FakeTrackerOptions } from "../fakes/tracker/server.js";
import { stubBinDir } from "../local/stack.js";
import { stubBinDir as offlineStubBinDir } from "../runner/process.js";
import { deleteProject } from "../safety/guards.js";
import { recordCreatedProject } from "../safety/sweep.js";
import { BareRemote } from "../testdata/bare-remote.js";
import { e2eName } from "../testdata/naming.js";
import { rand } from "../util/fs.js";
import { log } from "../util/log.js";

export type CaseId = `${string}-${number | string}`;

/** Attach catalogue case IDs to a test; the reporter maps results back to `catalog/index.json`. */
export function caseIds(...ids: CaseId[]) {
  return { annotation: ids.map((id) => ({ type: "case", description: id })) };
}

/** Settings that let a pipeline test run hands-free. Execution is always on the runner now. */
export const HANDS_FREE_SETTINGS: Partial<ProjectSettings> = {
  maxParallelTasks: 1,
  sameRepoParallelism: false,
  autoApprovePlan: true,
  autoApproveCodeReview: true,
  qaEnabled: false,
  manualVerificationEnabled: false,
  documentationSyncEnabled: false,
  automationEnabled: true,
};

export interface ProjectFactory {
  /** Create an `e2e-*` project, register it for cleanup, optionally patch settings. */
  create(suite: string, opts?: { description?: string; settings?: Partial<ProjectSettings> }): Promise<ProjectResponse>;
  /** Register an externally created project for cleanup. */
  track(project: ProjectResponse): void;
  /** Apply settings on top of the stored ones (PATCH replaces the whole settings JSON). */
  applySettings(project: ProjectResponse, settings: Partial<ProjectSettings>): Promise<ProjectResponse>;
  readonly created: ProjectResponse[];
}

export interface BareRemoteFactory {
  /** A fresh local bare remote seeded from a fixture; deleted after the test. */
  create(prefix: string, opts?: Parameters<typeof BareRemote.create>[1]): Promise<BareRemote>;
}

type Fixtures = {
  api: OrkkaApi;
  projects: ProjectFactory;
  /** The local stack's supervised runner is online with the capabilities the spec's tags need. */
  localRunner: void;
  keepProjects: boolean;
  fakeTrackerOptions: FakeTrackerOptions;
  /** A fake Jira Cloud + Azure DevOps server on 127.0.0.1, stopped after the test. */
  fakeTracker: FakeTrackerServer;
  /** The stub `gh` in the sandbox's bin dir, first on the stack runner's PATH; reset per test. */
  stubGh: StubGh;
  bareRemotes: BareRemoteFactory;
};

export const test = base.extend<Fixtures>({
  keepProjects: [false, { option: true }],
  fakeTrackerOptions: [{}, { option: true }],

  api: async ({}, use) => {
    await use(createApi());
  },

  projects: async ({ api, keepProjects }, use, testInfo) => {
    const created: ProjectResponse[] = [];
    const factory: ProjectFactory = {
      created,
      async create(suite, opts) {
        const name = e2eName(suite);
        const project = await api.projects.create({ name, description: opts?.description ?? `Orkka e2e ${suite} — ${testInfo.title}`, key: cfg.projectKey });
        recordCreatedProject(project, testInfo.title);
        created.push(project);
        testInfo.annotations.push({ type: "project", description: `${project.id} ${project.name}` });
        // GET /api/projects/{slug} has answered 404 for a short window right after creation
        // (slug→id cache); wait for it and record how long it took.
        const t0 = Date.now();
        await pollUntil({
          label: `slug ${project.slug} to resolve`,
          deadlineMs: 45_000,
          intervalMs: 1000,
          fetch: () => api.projects.get(project.slug).then(() => true).catch((e) => ((e as { status?: number }).status === 404 ? false : true)),
          done: (ok) => ok,
        });
        const slugMs = Date.now() - t0;
        if (slugMs > 1500) testInfo.annotations.push({ type: "finding", description: `slug ${project.slug} took ${slugMs} ms to resolve after creation` });
        return opts?.settings ? factory.applySettings(project, opts.settings) : project;
      },
      track(project) {
        recordCreatedProject(project, testInfo.title);
        created.push(project);
        testInfo.annotations.push({ type: "project", description: `${project.id} ${project.name}` });
      },
      async applySettings(project, settings) {
        const merged: ProjectSettings = { ...project.settings, ...settings } as ProjectSettings;
        return api.projects.patch(project.id, { settings: merged });
      },
    };
    await use(factory);
    if (keepProjects) {
      log.warn(`keepProjects=true — leaving ${created.length} project(s) in place`);
      return;
    }
    for (const p of created) {
      try {
        await deleteProject(api, p.id);
        log.info(`Deleted project ${p.name}`);
      } catch (e) {
        log.warn(`Cleanup: could not delete ${p.name}: ${(e as Error).message}`);
        testInfo.annotations.push({ type: "cleanup-failed", description: `${p.id} ${(e as Error).message}` });
      }
    }
  },

  localRunner: async ({ api }, use, testInfo) => {
    if (cfg.configured) {
      const { HYBRID_BASE_CAPS, TRACKER_CAPS, waitForRunner } = await import("../runner/presence.js");
      const tags = testInfo.tags.join(" ") + " " + testInfo.title;
      const r = await waitForRunner(api, /@tracker|@workflow/.test(tags) ? TRACKER_CAPS : HYBRID_BASE_CAPS, cfg.timeouts.runnerOnlineMs);
      testInfo.annotations.push({ type: "runner", description: `${r.machineName} v${r.daemonVersion}` });
    }
    await use();
  },

  fakeTracker: async ({ fakeTrackerOptions }, use, testInfo) => {
    const server = await new FakeTrackerServer({ port: env.E2E_FAKE_TRACKER_PORT || undefined, ...fakeTrackerOptions }).start();
    testInfo.annotations.push({ type: "fake-tracker", description: server.origin });
    await use(server);
    await server.stop();
  },

  stubGh: async ({}, use) => {
    // Offline specs get their own bin dir; with a stack configured it is the one on the runner's PATH.
    const stub = await StubGh.install(cfg.configured && env.E2E_SUITE !== "offline" ? stubBinDir() : offlineStubBinDir());
    await use(stub);
  },

  bareRemotes: async ({}, use) => {
    const made: BareRemote[] = [];
    await use({
      async create(prefix, opts) {
        const r = await BareRemote.create(`${prefix}-${Date.now().toString(36)}-${rand(4)}`.toLowerCase(), opts);
        made.push(r);
        return r;
      },
    });
    for (const r of made) {
      try {
        r.dispose();
      } catch (e) {
        log.warn(`could not remove bare remote ${r.dir}: ${(e as Error).message}`);
      }
    }
  },
});

export { expect };
export type { Page };
