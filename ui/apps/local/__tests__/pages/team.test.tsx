import { describe, it, expect, beforeEach, vi } from "vitest";

import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage } from "../render-page";

// Mock the AgentGrid client component
vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/team/AgentGrid",
  () => ({
    AgentGrid: (props: {
      agents: unknown[];
      repos: unknown[];
      models: { id: string }[];
      projectId: string;
      initialStats?: { window: string; agents: unknown[] } | null;
    }) =>
      `AgentGrid:agents=${props.agents.length},repos=${props.repos.length}` +
      `,models=${props.models.map((m) => m.id).join("|")}` +
      `,stats=${props.initialStats ? `${props.initialStats.window}:${props.initialStats.agents.length}` : "null"}`,
  })
);

// Client component with its own hooks and toast context — covered by its own test.
vi.mock("@/components/runner/ProviderMismatchBanner", () => ({
  ProviderMismatchBanner: (props: { agents: unknown[] }) =>
    `ProviderMismatchBanner:agents=${props.agents.length}`,
}));

import TeamPage from "@/app/[locale]/(dashboard)/p/[projectId]/team/page";

const routeParams = { projectId: "proj-1" };

describe("TeamPage", () => {
  beforeEach(() => {
    setRoute(routeParams);
    mockApi.agents.list.mockResolvedValue([
      { id: "a1", name: "Luna", role: "PM" },
      { id: "a2", name: "Pixel", role: "FrontendDev" },
    ]);
    mockApi.repos.list.mockResolvedValue([
      { id: "r1", name: "frontend-repo" },
    ]);
  });

  it("fetches agents and repos", async () => {
    await renderPage(<TeamPage />);

    expect(mockApi.agents.list).toHaveBeenCalledWith("proj-1", "");
    expect(mockApi.repos.list).toHaveBeenCalledWith("proj-1", "");
  });

  it("renders AgentGrid with fetched data", async () => {
    const { container } = await renderPage(<TeamPage />);

    expect(container.textContent).toContain("AgentGrid:agents=2,repos=1");
  });

  it("fetches the model catalog and passes every enabled chat model, runner-only rows included", async () => {
    const { container } = await renderPage(<TeamPage />);

    expect(mockApi.models.list).toHaveBeenCalledWith("");
    // copilot/auto is pickable when the user's runner has Copilot signed in — only the client
    // knows, so the page keeps it and AgentConfigTab disables it per row.
    expect(container.textContent).toContain("models=claude-sonnet-4-6|copilot/auto,");
  });

  it("still renders when the model catalog is unavailable", async () => {
    mockApi.models.list.mockRejectedValueOnce(new Error("catalog down"));

    const { container } = await renderPage(<TeamPage />);

    expect(container.textContent).toContain("AgentGrid:agents=2,repos=1");
    expect(container.textContent).toContain("models=,stats=");
  });

  it("fetches 7d agent stats and hands them to the grid", async () => {
    mockApi.agents.stats.mockResolvedValueOnce({
      window: "7d",
      from: "",
      to: "",
      agents: [{ agentId: "a1", runs: 3, succeeded: 2, failed: 1, running: 0 }],
    });

    const { container } = await renderPage(<TeamPage />);

    expect(mockApi.agents.stats).toHaveBeenCalledWith("proj-1", "7d", "");
    expect(container.textContent).toContain("stats=7d:1");
  });

  it("renders without stats when the stats endpoint fails", async () => {
    mockApi.agents.stats.mockRejectedValueOnce(new Error("stats down"));

    const { container } = await renderPage(<TeamPage />);

    expect(container.textContent).toContain("AgentGrid:agents=2,repos=1");
    expect(container.textContent).toContain("stats=null");
  });
});
