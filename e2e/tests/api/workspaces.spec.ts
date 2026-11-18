import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { createOwnerApi } from "../../src/auth/api-key.js";
import { ApiError } from "../../src/api/http.js";
import { needsEnvironment } from "../../config/index.js";

/**
 * Workspaces — one per job, all owned by the single local user. `X-Orkka-Workspace` is honoured
 * for the session and the install-secret bearer only (an API key is bound to its own workspace),
 * so the header cases use the install secret. The sandbox is disposable, so WSP-002 creates one.
 */
const NOT_A_MEMBER = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

test.describe("workspaces @api", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");
  test.describe.configure({ mode: "serial" });

  const ownerApi = () => createOwnerApi();

  test("WSP-001 GET /api/me/workspaces lists my workspaces with exactly one active", caseIds("WSP-001"), async ({ api }) => {
    const list = await api.workspaces.list();
    expect(list.length).toBeGreaterThan(0);
    expect(list.filter((w) => w.isActive)).toHaveLength(1);
    const me = await api.org.me();
    expect(list.map((w) => w.orgId)).toContain(me.orgId);
    for (const w of list) expect(w).toMatchObject({ orgId: expect.any(String), name: expect.any(String), slug: expect.any(String), role: expect.stringMatching(/Owner|Member/) });
  });

  test("WSP-003 switching the active workspace answers 204; an unknown org answers 404", caseIds("WSP-003"), async () => {
    const api = ownerApi();
    const before = await api.workspaces.list();
    const active = before.find((w) => w.isActive)!;
    await api.workspaces.setActive(active.orgId);
    expect((await api.workspaces.list()).find((w) => w.isActive)?.orgId).toBe(active.orgId);
    const err = await api.workspaces.setActive(NOT_A_MEMBER).catch((e) => e as ApiError);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(404);
  });

  test("WSP-004 a non-ULID X-Orkka-Workspace answers 400 Invalid workspace.", caseIds("WSP-004"), async () => {
    const err = await ownerApi().http.get("/api/org/me", { headers: { "X-Orkka-Workspace": "not-a-ulid" } }).catch((e) => e as ApiError);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(400);
    expect((err as ApiError).message).toMatch(/Invalid workspace/);
  });

  test("WSP-005 naming a workspace I am not in answers 403", caseIds("WSP-005"), async () => {
    const err = await ownerApi().inWorkspace(NOT_A_MEMBER).org.me().catch((e) => e as ApiError);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(403);
    expect((err as ApiError).message).toMatch(/not a member of that workspace/);
  });

  // Before WSP-006, which needs the second workspace this creates.
  test("WSP-002 creating a workspace makes it mine and active (restores the previous active one)", caseIds("WSP-002"), async () => {
    const api = ownerApi();
    const previous = (await api.workspaces.list()).find((w) => w.isActive)!;
    const created = await api.workspaces.create(`e2e-ws-${Date.now().toString(36)}`);
    try {
      expect(created).toMatchObject({ role: "Owner", isActive: true });
      const list = await api.workspaces.list();
      expect(list.find((w) => w.isActive)?.orgId).toBe(created.orgId);
      expect((await api.inWorkspace(created.orgId).projects.list())).toEqual([]);
    } finally {
      await api.workspaces.setActive(previous.orgId);
    }
    const err = await api.workspaces.create("").catch((e) => e as ApiError);
    expect((err as ApiError).status).toBe(400);
  });

  test("WSP-006 the same call in two workspaces returns disjoint projects", caseIds("WSP-006"), async ({ api }) => {
    const base = ownerApi();
    const first = (await api.org.me()).orgId;
    const second = (await base.workspaces.list()).find((w) => w.orgId !== first)?.orgId;
    test.skip(!second, "only one workspace exists (WSP-002 creates a second one)");
    const a = await base.inWorkspace(first).projects.list();
    const b = await base.inWorkspace(second!).projects.list();
    expect((await base.inWorkspace(second!).org.me()).orgId).toBe(second);
    const ids = new Set(a.map((p) => p.id));
    expect(b.filter((p) => ids.has(p.id))).toEqual([]);
  });

  test("WSP-007 an API key ignores X-Orkka-Workspace and stays in its own org", caseIds("WSP-007"), async ({ api }) => {
    const own = (await api.org.me()).orgId;
    expect((await api.inWorkspace(NOT_A_MEMBER).org.me()).orgId).toBe(own);
  });
});
