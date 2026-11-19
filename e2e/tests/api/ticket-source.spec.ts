import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { ApiError } from "../../src/api/http.js";
import { needsEnvironment } from "../../config/index.js";
import type { SaveProjectTrackerRequest } from "../../src/api/types.js";

/**
 * The project "Ticket source" endpoints and the tracker-connection relay, as far as they can be
 * exercised without a tracker connection (the happy path, TSRC-004/010, needs the sandboxed
 * runner holding a connection to the fake tracker — see tests/tracker/ticket-to-pr.spec.ts).
 */
const UNKNOWN = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

async function badRequest(p: Promise<unknown>): Promise<ApiError> {
  const err = await p.then(() => undefined).catch((e) => e as ApiError);
  expect(err, "expected a 400").toBeInstanceOf(ApiError);
  expect(err!.status).toBe(400);
  return err!;
}

test.describe("ticket source @api", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");

  test("TSRC-001 a project with no ticket source answers 204", caseIds("TSRC-001"), async ({ api, projects }) => {
    const p = await projects.create("tsrc");
    expect(await api.projects.tracker.get(p.id)).toBeUndefined();
    const res = await api.http.get<Response>(`/api/projects/${p.id}/tracker`, { raw: true });
    expect(res.status).toBe(204);
  });

  test("TSRC-002 PUT with a connection that is not mine → 400 on trackerConnectionId", caseIds("TSRC-002"), async ({ api, projects }) => {
    const p = await projects.create("tsrc");
    const repo = await api.repos.add(p.id, { name: "app", gitUrl: "https://github.com/e2e-local/app.git", defaultBranch: "main" });
    const err = await badRequest(api.projects.tracker.put(p.id, { trackerConnectionId: UNKNOWN, scope: "SHOP", epics: [{ epicKey: "SHOP-1", repoId: repo.id }] }));
    expect(JSON.stringify(err.body)).toMatch(/trackerConnectionId/i);
    expect(err.message).toMatch(/your own tracker connections|\.\/orkka tracker add/);
  });

  test("TSRC-003 validation: poll interval, branch template, epics, subfolder, scope", caseIds("TSRC-003"), async ({ api, projects }) => {
    const p = await projects.create("tsrc");
    const repo = await api.repos.add(p.id, { name: "app", gitUrl: "https://github.com/e2e-local/app.git", defaultBranch: "main" });
    const base: SaveProjectTrackerRequest = { trackerConnectionId: UNKNOWN, scope: "SHOP", epics: [{ epicKey: "SHOP-1", repoId: repo.id }] };
    const put = (patch: Partial<SaveProjectTrackerRequest>) => api.projects.tracker.put(p.id, { ...base, ...patch });

    expect((await badRequest(put({ pollIntervalSeconds: 30 }))).message).toMatch(/every 60.300 seconds/);
    expect((await badRequest(put({ pollIntervalSeconds: 301 }))).message).toMatch(/every 60.300 seconds/);
    expect((await badRequest(put({ branchTemplate: "feature/ticket" }))).message).toMatch(/must contain \{key\}/);
    expect((await badRequest(put({ epics: [], scopeMode: "Epics" }))).message).toMatch(/at least one epic/);
    expect((await badRequest(put({ epics: [{ epicKey: "SHOP-1", repoId: repo.id }, { epicKey: "shop-1", repoId: repo.id }] }))).message).toMatch(/mapped once/);
    for (const subpath of ["../outside", "C:/code", "/abs", "~/home", "a/./b"]) {
      expect((await badRequest(put({ epics: [{ epicKey: "SHOP-1", repoId: repo.id, subpath }] }))).message, subpath).toMatch(/relative path inside the repository/);
    }
    expect((await badRequest(put({ scope: "" }))).message).toMatch(/Pick the Jira project, Azure DevOps project or GitHub owner/);
  });

  test("TSRC-005 DELETE without a ticket source is a no-op 204; GET stays 204", caseIds("TSRC-005"), async ({ api, projects }) => {
    const p = await projects.create("tsrc");
    await api.projects.tracker.delete(p.id);
    expect(await api.projects.tracker.get(p.id)).toBeUndefined();
  });

  test("TSRC-007 tracker query with an unknown kind → 400", caseIds("TSRC-007"), async ({ api }) => {
    const err = await badRequest(api.trackers.query(UNKNOWN, { kind: "delete-everything" as never }));
    expect(err.message).toMatch(/Unknown query 'delete-everything'/);
  });

  test("TSRC-008 tracker query on an unknown connection → 404 (not 403)", caseIds("TSRC-008"), async ({ api }) => {
    const err = await api.trackers.query(UNKNOWN, { kind: "test" }).catch((e) => e as ApiError);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(404);
  });

  test("TSRC-009 GET /api/trackers/connections lists my connections with metadata only", caseIds("TSRC-009"), async ({ api }) => {
    const list = await api.trackers.connections();
    expect(Array.isArray(list)).toBe(true);
    for (const c of list) {
      expect(c).toMatchObject({ id: expect.any(String), key: expect.any(String), kind: expect.stringMatching(/^(jira|ado|github)$/), siteUrl: expect.any(String), machineName: expect.any(String) });
      expect(JSON.stringify(c), "no token-looking field").not.toMatch(/"(token|secret|pat|password)"/i);
    }
  });

  test("TSRC-006 a query while my runner is offline answers supported:false, not an error", caseIds("TSRC-006"), async ({ api }) => {
    const runner = await api.me.runner();
    const connections = await api.trackers.connections();
    test.skip(runner.online, "the runner is online — stop it (npm run runner:status) to exercise this case");
    test.skip(connections.length === 0, "no tracker connection reported yet (run the tracker suite once so the sandboxed runner reports one)");
    const res = await api.trackers.query(connections[0].id, { kind: "test" });
    expect(res).toMatchObject({ supported: false });
  });
});
