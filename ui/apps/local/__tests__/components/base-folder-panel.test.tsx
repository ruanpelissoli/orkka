import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, fireEvent, waitFor, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";

import { BaseFolderPanel } from "@/app/[locale]/(dashboard)/p/[projectId]/repos/BaseFolderPanel";
import type { ProjectRepo, RescanProjectResponse } from "@/lib/types";

function repo(overrides: Partial<ProjectRepo>): ProjectRepo {
  return {
    id: "r1",
    projectId: "p1",
    name: "api",
    gitUrl: "https://github.com/acme/api.git",
    defaultBranch: "main",
    provider: "github",
    workspaceStatus: "ready",
    cardColor: "#000",
    setupCommand: null,
    qaRunCommand: null,
    buildCommand: null,
    createdAt: "2026-11-01T00:00:00Z",
    folderPath: "api",
    description: null,
    missingSince: null,
    location: "remote",
    ...overrides,
  };
}

const REPOS = [
  repo({ description: "The backend API" }),
  repo({
    id: "r2",
    name: "web",
    gitUrl: "C:\\Work\\acme\\web",
    provider: "local",
    folderPath: "web",
    location: "local",
    missingSince: "2026-11-10T00:00:00Z",
  }),
];

const PROJECT = { baseFolder: "C:\\Work\\acme", baseFolderShape: "multirepo" as const };

function rescanResult(overrides: Partial<RescanProjectResponse> = {}): RescanProjectResponse {
  return {
    skipped: false,
    offline: false,
    added: [],
    missing: [],
    restored: [],
    originUpdated: [],
    warnings: [],
    repos: [],
    ...overrides,
  };
}

describe("BaseFolderPanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.projects.rescan.mockResolvedValue(rescanResult({ skipped: true, offline: true }));
  });

  it("shows the base folder and each repo's location and missing state", () => {
    renderWithPermissions(<BaseFolderPanel projectId="p1" project={PROJECT} repos={REPOS} />);

    expect(screen.getByTestId("base-folder-path")).toHaveTextContent("C:\\Work\\acme");
    const rows = screen.getAllByTestId("base-folder-repo");
    expect(within(rows[0]).getByText("remoteAt")).toBeInTheDocument();
    expect(within(rows[0]).getByText("The backend API")).toBeInTheDocument();
    expect(within(rows[1]).getByText("localFolder")).toBeInTheDocument();
    expect(within(rows[1]).getByText("missingSince")).toBeInTheDocument();
  });

  it("sends one automatic rescan on open and shows dirty/unpushed from it", async () => {
    mockApi.projects.rescan.mockResolvedValue(
      rescanResult({
        skipped: true,
        repos: [{ repoId: "r2", isDirty: true, hasUnpushedCommits: true, currentBranch: "dev" }],
      })
    );
    renderWithPermissions(<BaseFolderPanel projectId="p1" project={PROJECT} repos={REPOS} />);

    await waitFor(() =>
      expect(mockApi.projects.rescan).toHaveBeenCalledWith("p1", "", true)
    );
    expect(mockApi.projects.rescan).toHaveBeenCalledTimes(1);
    const rows = screen.getAllByTestId("base-folder-repo");
    expect(await within(rows[1]).findByText("dirty")).toBeInTheDocument();
    expect(within(rows[1]).getByText("unpushed")).toBeInTheDocument();
    // A throttled auto rescan is not news.
    expect(screen.queryByTestId("rescan-summary")).not.toBeInTheDocument();
  });

  it("does not rescan a project that has no base folder", () => {
    renderWithPermissions(
      <BaseFolderPanel projectId="p1" project={{ baseFolder: null, baseFolderShape: null }} repos={REPOS} />
    );
    expect(mockApi.projects.rescan).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "rescan" })).not.toBeInTheDocument();
  });

  it("summarises a manual rescan", async () => {
    renderWithPermissions(<BaseFolderPanel projectId="p1" project={PROJECT} repos={REPOS} />);
    await waitFor(() => expect(mockApi.projects.rescan).toHaveBeenCalledTimes(1));

    mockApi.projects.rescan.mockResolvedValue(
      rescanResult({ added: ["docs"], restored: ["web"], warnings: ["Skipped tools: it has no commits yet."] })
    );
    fireEvent.click(screen.getByRole("button", { name: "rescan" }));

    await waitFor(() => expect(mockApi.projects.rescan).toHaveBeenLastCalledWith("p1", ""));
    const summary = await screen.findByTestId("rescan-summary");
    expect(summary).toHaveTextContent("summaryAdded");
    expect(summary).toHaveTextContent("summaryRestored");
    expect(summary).toHaveTextContent("Skipped tools: it has no commits yet.");
  });

  it("explains a failed rescan", async () => {
    renderWithPermissions(<BaseFolderPanel projectId="p1" project={PROJECT} repos={REPOS} />);
    await waitFor(() => expect(mockApi.projects.rescan).toHaveBeenCalledTimes(1));

    mockApi.projects.rescan.mockRejectedValue(new Error("offline"));
    fireEvent.click(screen.getByRole("button", { name: "rescan" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("rescanFailed");
  });

  it("edits a repo's description inline", async () => {
    mockApi.repos.update.mockResolvedValue({ ...REPOS[1], description: "The web app" });
    renderWithPermissions(<BaseFolderPanel projectId="p1" project={PROJECT} repos={REPOS} />);

    const rows = screen.getAllByTestId("base-folder-repo");
    fireEvent.click(within(rows[1]).getByRole("button", { name: "addDescription" }));
    fireEvent.change(within(rows[1]).getByLabelText("descriptionLabel"), {
      target: { value: "  The web app " },
    });
    fireEvent.click(within(rows[1]).getByRole("button", { name: "actions.save" }));

    await waitFor(() =>
      expect(mockApi.repos.update).toHaveBeenCalledWith(
        "p1",
        "r2",
        { defaultBranch: "main", description: "The web app" },
        ""
      )
    );
    expect(await within(rows[1]).findByText("The web app")).toBeInTheDocument();
  });
});
