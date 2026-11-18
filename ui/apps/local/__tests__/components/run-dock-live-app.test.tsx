import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import type { AppRunSnapshot, Project } from "@/lib/types";

// The transport hook is the seam: the dock only sees snapshots and a start() function.
const hub = {
  snapshots: [] as AppRunSnapshot[],
  supported: true,
  attached: true,
  connected: true,
  start: vi.fn(),
  stop: vi.fn(),
  subscribeOutput: vi.fn(() => () => {}),
  sendInput: vi.fn(),
  sendResize: vi.fn(),
};
vi.mock("@/components/run/useAppRun", () => ({
  useAppRun: () => hub,
}));

// xterm touches window at import time; the log view is irrelevant to these assertions.
vi.mock("@/components/run/RunLogView", () => ({
  RunLogView: () => <div data-testid="run-log" />,
}));

const project = {
  id: "proj-1",
  orgId: "org-1",
  name: "Recipe Box",
  slug: "recipe-box",
  key: "RB",
  description: null,
  settings: { maxParallelTasks: 1, sameRepoParallelism: false },
  createdAt: "2026-01-01T00:00:00Z",
  updatedAt: "2026-01-01T00:00:00Z",
} as unknown as Project;
const projectCtx = { currentProject: project, refreshProject: vi.fn() };
vi.mock("@/hooks/use-project", () => ({
  useProject: () => projectCtx,
}));

vi.mock("@/hooks/use-runner-status", () => ({
  useRunnerStatus: () => ({ online: true, capabilities: ["app-run"] }),
}));

import { RunDock } from "@/components/run/RunDock";

function webRun(overrides: Partial<AppRunSnapshot> = {}): AppRunSnapshot {
  return {
    runId: "run-web",
    key: "web",
    label: "Web",
    kind: "web",
    repoId: "repo-1",
    port: 3000,
    url: "http://localhost:3000",
    status: "running",
    exitCode: null,
    error: null,
    portConflict: false,
    startedAt: "2026-01-01T00:00:00Z",
    scrollback: null,
    ready: true,
    previewUrl: "http://localhost:3000",
    ...overrides,
  };
}

describe("RunDock — live app", () => {
  beforeEach(() => {
    hub.snapshots = [];
    hub.start.mockReset();
    projectCtx.currentProject = { ...project, settings: { ...project.settings } };
  });

  it("offers the running web app's address as a link once its port answers", () => {
    hub.snapshots = [webRun()];

    render(<RunDock />);

    const links = screen.getAllByRole("link", { name: "openLive" });
    expect(links.length).toBeGreaterThan(0);
    expect(links[0]).toHaveAttribute("href", "http://localhost:3000");
    expect(links[0]).toHaveAttribute("target", "_blank");
  });

  it("says starting… — no link — until the port is ready", () => {
    hub.snapshots = [webRun({ ready: false })];

    render(<RunDock />);

    expect(screen.queryByRole("link", { name: "openLive" })).toBeNull();
    expect(screen.getAllByText("starting").length).toBeGreaterThan(0);
  });

  it("prefers the web app over an API when both are up", () => {
    hub.snapshots = [
      webRun({ runId: "run-api", key: "api", kind: "api", port: 8080,
        url: "http://localhost:8080", previewUrl: "http://localhost:8080" }),
      webRun(),
    ];

    render(<RunDock />);

    expect(screen.getAllByRole("link", { name: "openLive" })[0])
      .toHaveAttribute("href", "http://localhost:3000");
  });

  it("shows no live chip for targets a browser cannot open", () => {
    hub.snapshots = [webRun({ kind: "infra", url: null, previewUrl: null, port: 5432 })];

    render(<RunDock />);

    expect(screen.queryByRole("link", { name: "openLive" })).toBeNull();
    expect(screen.queryByText("starting")).toBeNull();
  });

  it("auto-starts the auto-select targets when the project asks to be kept running", async () => {
    projectCtx.currentProject = {
      ...project,
      settings: { ...project.settings, autoRunOnOpen: true },
    };
    hub.start.mockResolvedValue([webRun()]);

    render(<RunDock />);

    await waitFor(() => expect(hub.start).toHaveBeenCalledWith([], false));
    expect(hub.start).toHaveBeenCalledTimes(1);
  });

  it("does not auto-start when something is already running, or when the project did not ask", () => {
    projectCtx.currentProject = {
      ...project,
      settings: { ...project.settings, autoRunOnOpen: true },
    };
    hub.snapshots = [webRun()];
    render(<RunDock />);
    expect(hub.start).not.toHaveBeenCalled();

    projectCtx.currentProject = { ...project };
    hub.snapshots = [];
    render(<RunDock />);
    expect(hub.start).not.toHaveBeenCalled();
  });

  it("saves the keep-running toggle as a full settings replacement", async () => {
    mockApi.projects.update.mockResolvedValue(project);
    render(<RunDock />);

    const toggle = screen.getByRole("switch");
    toggle.click();

    await waitFor(() =>
      expect(mockApi.projects.update).toHaveBeenCalledWith(
        "proj-1",
        { settings: { maxParallelTasks: 1, sameRepoParallelism: false, autoRunOnOpen: true } },
        ""
      )
    );
    await waitFor(() => expect(projectCtx.refreshProject).toHaveBeenCalledWith("proj-1"));
  });
});
