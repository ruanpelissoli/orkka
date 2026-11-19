import { describe, it, expect, beforeEach, vi } from "vitest";
import { fireEvent, screen, waitFor, within, render } from "@testing-library/react";
import React from "react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { mockUpdateSearchParams, setRoute } from "../mocks/navigation";
import { mockI18nRouter } from "../mocks/i18n-navigation";
import { renderPageWithPermissions } from "../render-page";
import type { MyRunnerStatus, Project, ProjectRepo, ProjectTrackerConfig, TrackerConnection } from "@/lib/types";

// The settings card itself has its own tests; here it only has to report a save.
vi.mock("@/components/project/TicketSourceCard", () => ({
  TicketSourceCard: (props: {
    connections: TrackerConnection[];
    onSaved?: (config: ProjectTrackerConfig | null) => void;
  }) =>
    React.createElement(
      "div",
      { "data-testid": "ticket-source-card-stub" },
      `connections:${props.connections.map((c) => c.kind).join(",")}`,
      React.createElement("button", { type: "button", onClick: () => props.onSaved?.(SOURCE) }, "stub-save")
    ),
}));
vi.mock("@/app/[locale]/(dashboard)/p/[projectId]/team/TeamModelControl", () => ({
  TeamModelControl: () => React.createElement("div", { "data-testid": "team-model-control" }),
}));

import ProjectSetupPage from "@/app/[locale]/(dashboard)/p/[projectId]/setup/page";
import { FinishSetupBanner } from "@/components/project-setup/FinishSetupBanner";

const JIRA: TrackerConnection = {
  id: "conn-1",
  key: "jira-acme",
  kind: "jira",
  siteUrl: "https://acme.atlassian.net",
  accountId: "a1",
  displayName: "Dana",
  healthy: true,
  healthDetail: null,
  machineName: "dana-laptop",
  lastSeenAt: "2026-11-19T00:00:00Z",
  checkedAt: null,
};

const SOURCE = {
  id: "cfg-1",
  projectId: "proj-1",
  ownerUserId: "local_user",
  trackerConnectionId: "conn-1",
  connection: JIRA,
  scope: "SHOP",
  enabled: true,
  lastPollError: null,
} as unknown as ProjectTrackerConfig;

function project(overrides: Partial<Project> = {}): Project {
  return {
    id: "proj-1",
    orgId: "org-1",
    name: "Shop",
    slug: "shop",
    key: "SHOP",
    description: null,
    settings: {} as Project["settings"],
    createdAt: "2026-11-19T00:00:00Z",
    updatedAt: "2026-11-19T00:00:00Z",
    baseFolder: "C:\\Work\\shop",
    baseFolderShape: "monorepo",
    setupCompletedAt: null,
    setupSteps: [],
    ...overrides,
  };
}

const LOCAL_REPO = {
  id: "repo-1",
  projectId: "proj-1",
  name: "shop",
  gitUrl: "C:\\Work\\shop",
  defaultBranch: "main",
  provider: "local",
  location: "local",
  workspaceStatus: "Ready",
  cardColor: "#fff",
  setupCommand: null,
  qaRunCommand: null,
  buildCommand: null,
  checkCommand: null,
  createdAt: "2026-11-19T00:00:00Z",
  folderPath: "",
} as unknown as ProjectRepo;

const READY_RUNNER = {
  online: true,
  machineName: "dana-laptop",
  daemonVersion: "1",
  connectedAt: null,
  lastSeenAt: null,
  providers: [{ provider: "claude", cliFound: true, authenticated: true, models: null, detail: null }],
  lastKnownProviders: null,
  lastKnownAt: null,
  lastKnownMachineName: null,
  hasActiveTokens: true,
  forgeClis: [],
  commitIdentity: { name: "Dana", email: "dana@acme.com", source: "workspace" },
} as unknown as MyRunnerStatus;

function stepButton(step: string) {
  return screen.getByTestId(`setup-step-${step}`);
}

describe("project setup page", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.history.replaceState(null, "", "/en/p/shop/setup");
    setRoute({ projectId: "shop" });
    mockApi.projects.get.mockResolvedValue(project());
    mockApi.repos.list.mockResolvedValue([LOCAL_REPO]);
    mockApi.trackers.getProjectConfig.mockResolvedValue(null);
    mockApi.trackers.connections.mockResolvedValue([]);
    mockApi.runner.status.mockResolvedValue(READY_RUNNER);
    mockApi.repos.accessCheck.mockResolvedValue({ supported: true, repos: [] });
    mockApi.projects.setupCommands.mockResolvedValue({ supported: true, repos: [] });
    mockApi.projects.setSetupStep.mockResolvedValue(project({ setupSteps: ["folder"] }));
    mockApi.projects.startSetup.mockResolvedValue(project({ setupCompletedAt: "2026-11-19T10:00:00Z" }));
  });

  it("resumes at the first step not confirmed, and marks the confirmed ones", async () => {
    mockApi.projects.get.mockResolvedValue(project({ setupSteps: ["folder"] }));
    await renderPageWithPermissions(<ProjectSetupPage />);

    expect(stepButton("machine")).toHaveAttribute("aria-current", "step");
    expect(stepButton("folder")).toHaveAttribute("data-done", "true");
    expect(stepButton("source")).toHaveAttribute("data-done", "false");
    expect(await screen.findByTestId("setup-machine-step")).toBeInTheDocument();
  });

  it("opens the step the URL names, and Continue confirms it and moves on", async () => {
    setRoute({ projectId: "shop" }, "step=folder");
    await renderPageWithPermissions(<ProjectSetupPage />);

    expect(screen.getByTestId("setup-folder-step")).toBeInTheDocument();
    expect(screen.getByTestId("setup-repo-card")).toHaveAttribute("data-repo", "shop");
    expect(screen.getByTestId("setup-repo-mode")).toHaveTextContent("modeLocal");

    fireEvent.click(screen.getByTestId("setup-continue"));
    await waitFor(() => expect(mockApi.projects.setSetupStep).toHaveBeenCalledWith("proj-1", "folder", true, ""));
    await waitFor(() => expect(mockUpdateSearchParams).toHaveBeenCalled());
    expect(new URL(window.location.href).searchParams.get("step")).toBe("machine");
  });

  it("the machine check lists what the runner reported, and Re-check asks again", async () => {
    setRoute({ projectId: "shop" }, "step=machine");
    mockApi.runner.status.mockResolvedValue({ ...READY_RUNNER, online: false });
    await renderPageWithPermissions(<ProjectSetupPage />);

    const list = await screen.findByTestId("setup-machine-checks");
    const runnerCheck = within(list).getAllByTestId("setup-check").find((el) => el.dataset.check === "machine.runner")!;
    expect(runnerCheck).toHaveAttribute("data-level", "error");
    expect(runnerCheck).toHaveTextContent("./orkka start");

    const calls = mockApi.runner.status.mock.calls.length;
    fireEvent.click(screen.getByTestId("setup-recheck"));
    await waitFor(() => expect(mockApi.runner.status.mock.calls.length).toBeGreaterThan(calls));
    expect(mockApi.projects.setupCommands).toHaveBeenCalled();
  });

  it("the source step connects a tracker inline when this machine has none of that kind", async () => {
    setRoute({ projectId: "shop" }, "step=source");
    await renderPageWithPermissions(<ProjectSetupPage />);

    expect(await screen.findByTestId("setup-source-connect")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("setup-source-github"));
    expect(screen.getByTestId("setup-source-connect")).toHaveTextContent("connectTitle");
  });

  it("with a connection, embeds the ticket source card and previews what would be pulled", async () => {
    setRoute({ projectId: "shop" }, "step=source");
    mockApi.trackers.connections.mockResolvedValue([JIRA]);
    mockApi.trackers.previewIntake.mockResolvedValue({
      supported: true,
      ok: true,
      preview: {
        newTaskCount: 3,
        tasks: [
          { key: "SHOP-1", title: "Export", type: "Story", status: "To Do", url: null, assignee: "Dana",
            repoId: "repo-1", repoName: "shop", spansRepos: [], route: "single-repo", reason: null },
        ],
        stories: [
          {
            story: { key: "SHOP-10", title: "Checkout", type: "Story", status: "To Do", url: null, assignee: "Dana",
              repoId: null, repoName: null, spansRepos: [], route: "story", reason: null },
            branch: "feature/SHOP-10-checkout",
            members: [
              { key: "SHOP-11", title: "Part 1", type: "Sub-task", status: "To Do", url: null, assignee: "Dana",
                repoId: "repo-1", repoName: "shop", spansRepos: [], route: "single-repo", reason: null },
              { key: "SHOP-12", title: "Part 2", type: "Sub-task", status: "To Do", url: null, assignee: "Dana",
                repoId: null, repoName: null, spansRepos: [], route: "router-at-import", reason: "picked later" },
            ],
          },
        ],
        alreadyTracked: [],
        skipped: [{ key: "SHOP-13", title: "Someone else's", status: "To Do", url: null, reason: "notAssignedToMe" }],
      },
    });
    await renderPageWithPermissions(<ProjectSetupPage />);

    expect(await screen.findByTestId("ticket-source-card-stub")).toHaveTextContent("connections:jira");
    expect(screen.queryByTestId("setup-intake-preview")).toBeNull();

    // Saving the source makes the dry run available.
    fireEvent.click(screen.getByText("stub-save"));
    fireEvent.click(await screen.findByTestId("setup-preview-run"));

    await waitFor(() => expect(mockApi.trackers.previewIntake).toHaveBeenCalledWith("proj-1", ""));
    expect(await screen.findByTestId("setup-preview-count")).toHaveTextContent("count");
    expect(screen.getByTestId("setup-preview-tasks")).toHaveTextContent("SHOP-1");
    const story = screen.getByTestId("setup-preview-story");
    expect(within(story).getAllByTestId("setup-preview-ticket").map((r) => r.textContent)).toEqual([
      expect.stringContaining("SHOP-11"),
      expect.stringContaining("SHOP-12"),
    ]);
    expect(within(story).getByText("repoAtImport")).toBeInTheDocument();
    expect(screen.getByTestId("setup-preview-skipped")).toHaveTextContent("SHOP-13");
  });

  it("None removes an existing ticket source", async () => {
    setRoute({ projectId: "shop" }, "step=source");
    mockApi.trackers.getProjectConfig.mockResolvedValue(SOURCE);
    mockApi.trackers.connections.mockResolvedValue([JIRA]);
    await renderPageWithPermissions(<ProjectSetupPage />);

    fireEvent.click(await screen.findByTestId("setup-source-none"));
    fireEvent.click(within(screen.getByTestId("setup-source-none-panel")).getByRole("button"));
    await waitFor(() => expect(mockApi.trackers.deleteProjectConfig).toHaveBeenCalledWith("proj-1", ""));
  });

  it("Start is disabled while anything is red; Start anyway lists the warnings and starts", async () => {
    setRoute({ projectId: "shop" }, "step=review");
    mockApi.runner.status.mockResolvedValue({ ...READY_RUNNER, online: false });
    await renderPageWithPermissions(<ProjectSetupPage />);

    await waitFor(() =>
      expect(
        within(screen.getByTestId("setup-review-checks"))
          .getAllByTestId("setup-check")
          .some((el) => el.dataset.check === "machine.runner")
      ).toBe(true)
    );
    expect(screen.getByTestId("setup-start")).toBeDisabled();

    fireEvent.click(screen.getByTestId("setup-start-anyway"));
    const confirm = screen.getByTestId("setup-start-anyway-confirm");
    expect(within(confirm).getByText("checks.runnerOffline")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("setup-start-anyway-confirm-button"));

    await waitFor(() => expect(mockApi.projects.startSetup).toHaveBeenCalled());
    const [id, warnings] = mockApi.projects.startSetup.mock.calls[0];
    expect(id).toBe("proj-1");
    expect(warnings).toContain("checks.runnerOffline");
    await waitFor(() => expect(mockI18nRouter.push).toHaveBeenCalledWith("/p/shop/board"));
  });

  it("with everything green, Start starts at once", async () => {
    setRoute({ projectId: "shop" }, "step=review");
    // Every step confirmed, the source is "none" (manual tasks only), the machine is ready.
    mockApi.projects.get.mockResolvedValue(project({ setupSteps: ["folder", "machine", "source", "team"] }));
    await renderPageWithPermissions(<ProjectSetupPage />);

    await waitFor(() => expect(screen.getByTestId("setup-start")).not.toBeDisabled());
    expect(screen.queryByTestId("setup-start-anyway")).toBeNull();
    fireEvent.click(screen.getByTestId("setup-start"));
    await waitFor(() => expect(mockApi.projects.startSetup).toHaveBeenCalledWith("proj-1", [], ""));
  });

  it("a started project shows its checks again without a Start button", async () => {
    setRoute({ projectId: "shop" }, "step=review");
    mockApi.projects.get.mockResolvedValue(
      project({ setupCompletedAt: "2026-11-01T00:00:00Z", setupSteps: ["folder", "machine", "source", "team"] })
    );
    await renderPageWithPermissions(<ProjectSetupPage />);

    expect(await screen.findByTestId("setup-started")).toBeInTheDocument();
    expect(screen.queryByTestId("setup-start")).toBeNull();
  });
});

describe("FinishSetupBanner", () => {
  it("shows on a project not started, linking to its setup", () => {
    render(<FinishSetupBanner project={{ slug: "shop", setupCompletedAt: null }} />);
    expect(screen.getByTestId("finish-setup-banner")).toBeInTheDocument();
    expect(screen.getByRole("link")).toHaveAttribute("href", "/p/shop/setup");
  });

  it("is absent once started, and for an API older than the setup flow", () => {
    const { rerender } = render(
      <FinishSetupBanner project={{ slug: "shop", setupCompletedAt: "2026-11-19T00:00:00Z" }} />
    );
    expect(screen.queryByTestId("finish-setup-banner")).toBeNull();
    rerender(<FinishSetupBanner project={{ slug: "shop" } as { slug: string; setupCompletedAt: null }} />);
    expect(screen.queryByTestId("finish-setup-banner")).toBeNull();
  });
});
