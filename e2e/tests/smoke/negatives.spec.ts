import { test, expect, caseIds } from "../../src/fixtures/test.js";
import { ApiError } from "../../src/api/http.js";
import { cfg, needsEnvironment } from "../../config/index.js";
import { recordCreatedProject } from "../../src/safety/sweep.js";
import { stamp, rand } from "../../src/util/fs.js";

test.describe("cross-cutting negatives @smoke", () => {
  test.skip(Boolean(needsEnvironment()), needsEnvironment() ?? "");
  test("NEG-030 a project whose slug is exactly 26 characters must still resolve by slug (ULID-length regression)", caseIds("NEG-030"), async ({ api, projects }) => {
    // Fixed 2026-11-19: ProjectResolver reads a 26-character value as an id only in Orkka's own
    // uppercase spelling; anything else is looked up as a slug first.
    // Build a name whose slug is exactly 26 chars: "e2e-neg-" (8) + 18 chars.
    const tail = `${stamp()}${rand(5)}`.slice(0, 18); // yyyymmdd-HHmm = 13 + 5 = 18
    const name = `${cfg.projectPrefix}neg-${tail}`;
    expect(name.length).toBe(26);
    const project = await api.projects.create({ name, description: "26-character slug regression (see NEG-030)", key: cfg.projectKey });
    recordCreatedProject(project, test.info().title);
    projects.track(project);
    expect(project.slug.length).toBe(26);

    const byId = await api.projects.get(project.id);
    expect(byId.slug).toBe(project.slug);

    const bySlug = await api.projects.get(project.slug).catch((e) => e as ApiError);
    if (bySlug instanceof ApiError) {
      test.info().annotations.push({
        type: "finding",
        description: `BUG: GET /api/projects/${project.slug} → ${bySlug.status}. A 26-character slug is swallowed by Ulid.TryParse in ProjectResolver.ResolveAsync (orkka-api/src/Modules/Orkka.Modules.Projects/PublicApi/ProjectResolver.cs:36) so the project is unreachable by slug: board empty, settings page 500, task pages "Task not found".`,
      });
    }
    expect(bySlug, "project reachable by its 26-char slug").not.toBeInstanceOf(ApiError);
  });

  test("NEG-031 the removed cloud-execution surface is gone: routes 404", caseIds("NEG-031"), async ({ api, projects }) => {
    const status = (e: unknown) => (e instanceof ApiError ? e.status : 200);
    const project = await projects.create("neg");
    const calls: [string, () => Promise<unknown>][] = [
      ["GET /api/me/execution-preference", () => api.http.get("/api/me/execution-preference", { retry: false })],
      ["PUT /api/me/execution-preference", () => api.http.put("/api/me/execution-preference", { target: "MyRunner" }, { retry: false })],
      ["POST /api/projects/{id}/tasks/execution-target", () => api.http.post(`/api/projects/${project.id}/tasks/execution-target`, {}, { retry: false })],
    ];
    for (const [label, call] of calls) {
      const err = await call().then(() => undefined, (e) => e);
      expect([404, 405], `${label} must no longer exist`).toContain(status(err));
    }
  });

  test("an unknown project id answers 404, not 403", async ({ api }) => {
    const err = await api.projects.get("01ARZ3NDEKTSV4RRFFQ69G5FAV").catch((e) => e as ApiError);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(404);
  });

  test("API keys cannot mint API keys", async ({ api }) => {
    const err = await api.me.apiKeys.create({ name: "e2e-should-fail", permissions: ["TasksCreate"] }).catch((e) => e as ApiError);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(403);
  });

  test("health endpoint answers anonymously", async () => {
    const res = await fetch(new URL("/health", cfg.apiUrl));
    expect(res.status).toBe(200);
  });

  test("the local host refuses a foreign Host header (DNS rebinding) with 421", async () => {
    const http = await import("node:http");
    const u = new URL("/health", cfg.apiUrl);
    const status = await new Promise<number>((resolve, reject) => {
      http.get({ host: u.hostname, port: u.port, path: u.pathname, headers: { Host: "evil.example:6750" } }, (r) => resolve(r.statusCode ?? 0)).on("error", reject);
    });
    expect(status).toBe(421);
  });

  test("a cookie-authenticated write without Origin + X-Orkka-Csrf is refused", async ({ page }) => {
    await page.goto("/en/dashboard");
    const status = await page.evaluate(async () => (await fetch("/api/me/active-workspace", { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" })).status);
    // The browser sends Origin on a same-origin POST but never the CSRF header.
    expect(status).toBe(403);
  });

});
