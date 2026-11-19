import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, matchRoutes, RouterProvider } from "react-router";
import "../mocks/api";
import "../mocks/signalr";
import { routes } from "@/src/routes";

/** The full pattern of the deepest route matching `path`, e.g. "/:locale/p/:projectId/board". */
function patternOf(path: string): string | null {
  const matches = matchRoutes(routes, path);
  if (!matches) return null;
  return (
    "/" +
    matches
      .map((m) => m.route.path ?? "")
      .filter(Boolean)
      .map((p) => p.replace(/^\//, ""))
      .join("/")
  );
}

function start(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  render(<RouterProvider router={router} />);
  return router;
}

describe("route table", () => {
  beforeEach(() => {
    document.cookie = "NEXT_LOCALE=; path=/; max-age=0";
  });

  it.each([
    ["/en", "/:locale"],
    ["/en/dashboard", "/:locale/dashboard"],
    ["/en/board", "/:locale/board"],
    ["/en/inbox", "/:locale/inbox"],
    ["/en/notifications", "/:locale/notifications"],
    ["/en/projects", "/:locale/projects"],
    ["/en/projects/new", "/:locale/projects/new"],
    ["/en/p/01ABC", "/:locale/p/:projectId"],
    ["/pt/p/01ABC/board", "/:locale/p/:projectId/board"],
    ["/en/p/01ABC/activity", "/:locale/p/:projectId/activity"],
    ["/en/p/01ABC/architect", "/:locale/p/:projectId/architect"],
    ["/en/p/01ABC/automation", "/:locale/p/:projectId/automation"],
    ["/en/p/01ABC/design", "/:locale/p/:projectId/design"],
    ["/en/p/01ABC/documents", "/:locale/p/:projectId/documents"],
    ["/en/p/01ABC/memory", "/:locale/p/:projectId/memory"],
    ["/en/p/01ABC/repos", "/:locale/p/:projectId/repos"],
    ["/en/p/01ABC/roadmap", "/:locale/p/:projectId/roadmap"],
    ["/en/p/01ABC/roadmap/m1", "/:locale/p/:projectId/roadmap/:milestoneId"],
    ["/en/p/01ABC/settings", "/:locale/p/:projectId/settings"],
    ["/en/p/01ABC/setup", "/:locale/p/:projectId/setup"],
    ["/en/p/01ABC/skills", "/:locale/p/:projectId/skills"],
    ["/en/p/01ABC/tasks", "/:locale/p/:projectId/tasks"],
    ["/en/p/01ABC/tasks/new", "/:locale/p/:projectId/tasks/new"],
    ["/en/p/01ABC/tasks/12", "/:locale/p/:projectId/tasks/:taskId"],
    ["/en/p/01ABC/tasks/12/merge", "/:locale/p/:projectId/tasks/:taskId/merge"],
    ["/en/p/01ABC/tasks/12/review", "/:locale/p/:projectId/tasks/:taskId/review"],
    ["/en/p/01ABC/team", "/:locale/p/:projectId/team"],
    ["/en/projects/01ABC", "/:locale/projects/:projectId"],
    ["/en/projects/01ABC/board", "/:locale/projects/:projectId/board"],
    ["/en/projects/01ABC/repos", "/:locale/projects/:projectId/repos"],
    ["/en/projects/01ABC/settings", "/:locale/projects/:projectId/settings"],
    ["/en/projects/01ABC/tasks/new", "/:locale/projects/:projectId/tasks/new"],
    ["/en/projects/01ABC/tasks/12", "/:locale/projects/:projectId/tasks/:taskId"],
    ["/en/settings", "/:locale/settings"],
    ["/en/settings/api-keys", "/:locale/settings/api-keys"],
    ["/en/settings/machine", "/:locale/settings/machine"],
    ["/en/settings/notifications", "/:locale/settings/notifications"],
    ["/en/settings/preferences", "/:locale/settings/preferences"],
    ["/en/settings/trackers", "/:locale/settings/trackers"],
    ["/en/settings/usage", "/:locale/settings/usage"],
    ["/en/settings/workspace", "/:locale/settings/workspace"],
    ["/en/no/such/page", "/:locale/*"],
    ["/en/p/01ABC/nope", "/:locale/*"],
  ])("%s resolves to %s", (path, pattern) => {
    expect(patternOf(path)).toBe(pattern);
  });

  it("sends / to the workspace board in the preferred locale", async () => {
    document.cookie = "NEXT_LOCALE=fr; path=/";
    const router = start("/");
    await waitFor(() => expect(router.state.location.pathname).toBe("/fr/board"));
  });

  it("redirects an unprefixed deep link client-side, keeping its query", async () => {
    const router = start("/p/01ABC/board?task=3");
    await waitFor(() => expect(router.state.location.pathname).toBe("/en/p/01ABC/board"));
    expect(router.state.location.search).toBe("?task=3");
  });

  it("uses the locale cookie for an unprefixed path", async () => {
    document.cookie = "NEXT_LOCALE=pt; path=/";
    const router = start("/dashboard");
    await waitFor(() => expect(router.state.location.pathname).toBe("/pt/dashboard"));
  });

  it("forwards an old /projects URL to its /p page", async () => {
    const router = start("/de/projects/01ABC/board");
    await waitFor(() => expect(router.state.location.pathname).toBe("/de/p/01ABC/board"));
  });

  it("remembers the locale of the page in the NEXT_LOCALE cookie", async () => {
    start("/es/no/such/page");
    await waitFor(() => expect(document.cookie).toContain("NEXT_LOCALE=es"));
    expect(document.documentElement.lang).toBe("es");
  });

  it("renders the translated 404 page for an unknown path", async () => {
    start("/en/no/such/page");
    expect(await screen.findByText("404")).toBeInTheDocument();
  });
});
