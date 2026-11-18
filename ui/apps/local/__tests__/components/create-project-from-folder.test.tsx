import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, fireEvent, waitFor, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";

import { CreateProjectFromFolder } from "@/components/projects/CreateProjectFromFolder";
import type { DiscoveredRepo, DiscoverReposResponse } from "@/lib/types";

const LISTING = {
  supported: true,
  path: "C:\\Work",
  parentPath: null,
  entries: [{ name: "acme", fullPath: "C:\\Work\\acme", isGitRepo: false }],
  error: null,
};

function repo(overrides: Partial<DiscoveredRepo>): DiscoveredRepo {
  return {
    relativePath: "api",
    name: "api",
    originUrl: "https://github.com/acme/api.git",
    location: "remote",
    forge: "github",
    host: "github.com",
    defaultBranch: "main",
    currentBranch: "main",
    isDirty: false,
    hasUnpushedCommits: false,
    hasCommits: true,
    description: null,
    stack: null,
    ...overrides,
  };
}

const MULTI: DiscoverReposResponse = {
  supported: true,
  path: "C:\\Work\\acme",
  shape: "multirepo",
  areas: [],
  error: null,
  repos: [
    repo({ stack: ".NET", description: "The backend API" }),
    repo({
      relativePath: "web",
      name: "web",
      originUrl: null,
      location: "local",
      forge: null,
      host: null,
      currentBranch: "feature/x",
      isDirty: true,
      hasUnpushedCommits: true,
      stack: "Next.js",
    }),
  ],
};

/** Opens the picker, selects the "acme" folder and confirms it. */
async function pickAcme() {
  fireEvent.click(screen.getByRole("button", { name: "chooseFolder" }));
  fireEvent.click(await screen.findByText("acme"));
  fireEvent.click(screen.getByRole("button", { name: "folderPicker.useFolder" }));
}

describe("CreateProjectFromFolder", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.runnerFs.browse.mockResolvedValue(LISTING);
    mockApi.runnerFs.discover.mockResolvedValue(MULTI);
  });

  it("previews what the runner finds in the picked folder", async () => {
    renderWithPermissions(<CreateProjectFromFolder onCreated={() => {}} />);
    await pickAcme();

    await waitFor(() =>
      expect(mockApi.runnerFs.discover).toHaveBeenCalledWith("C:\\Work\\acme", "")
    );
    expect(screen.getByTestId("from-folder-path")).toHaveTextContent("C:\\Work\\acme");
    expect(await screen.findByTestId("from-folder-shape")).toHaveTextContent("shapeMultiRepo");

    const rows = screen.getAllByTestId("from-folder-repo");
    expect(rows).toHaveLength(2);

    // A repo with an origin: Remote badge, its stack and its description.
    expect(within(rows[0]).getByText("remoteAt")).toBeInTheDocument();
    expect(within(rows[0]).getByText(".NET")).toBeInTheDocument();
    expect(within(rows[0]).getByText("The backend API")).toBeInTheDocument();
    expect(within(rows[0]).queryByText("dirty")).not.toBeInTheDocument();

    // A repo without one: Local folder badge, with its dirty/unpushed warnings.
    expect(within(rows[1]).getByText("localFolder")).toBeInTheDocument();
    expect(within(rows[1]).getByText("dirty")).toBeInTheDocument();
    expect(within(rows[1]).getByText("unpushed")).toBeInTheDocument();

    // The name defaults to the folder's.
    expect(screen.getByLabelText("nameLabel")).toHaveValue("acme");
  });

  it("labels a monorepo as one repository", async () => {
    mockApi.runnerFs.discover.mockResolvedValue({
      ...MULTI,
      shape: "monorepo",
      repos: [repo({ relativePath: "", name: "acme" })],
    });
    renderWithPermissions(<CreateProjectFromFolder onCreated={() => {}} />);
    await pickAcme();

    expect(await screen.findByTestId("from-folder-shape")).toHaveTextContent("shapeMonorepo");
    expect(screen.getByText("rootPath")).toBeInTheDocument();
  });

  it("says the runner is offline instead of showing a preview", async () => {
    mockApi.runnerFs.discover.mockResolvedValue({
      supported: false,
      path: null,
      shape: null,
      repos: [],
      areas: [],
      error: "Your runner is not running",
    });
    renderWithPermissions(<CreateProjectFromFolder onCreated={() => {}} />);
    await pickAcme();

    expect(await screen.findByText("runnerOffline")).toBeInTheDocument();
    expect(screen.queryByTestId("from-folder-repos")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "create" })).toBeDisabled();
  });

  it("creates the project from the folder with the chosen name, then hands it back", async () => {
    const project = {
      id: "p1",
      name: "Acme",
      slug: "acme",
      key: "ACM",
      baseFolder: "C:\\Work\\acme",
      baseFolderShape: "multirepo",
    };
    mockApi.projects.createFromFolder.mockResolvedValue({ project, repos: [], warnings: [] });
    const onCreated = vi.fn();
    renderWithPermissions(<CreateProjectFromFolder onCreated={onCreated} />);
    await pickAcme();

    const name = await screen.findByLabelText("nameLabel");
    fireEvent.change(name, { target: { value: "Acme" } });
    fireEvent.click(screen.getByRole("button", { name: "create" }));

    await waitFor(() =>
      expect(mockApi.projects.createFromFolder).toHaveBeenCalledWith(
        { path: "C:\\Work\\acme", name: "Acme" },
        ""
      )
    );
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith(project));
  });

  it("shows the warnings before moving on when the project was created with some", async () => {
    const project = {
      id: "p1",
      name: "acme",
      slug: "acme",
      key: "ACM",
      baseFolder: "C:\\Work\\acme",
      baseFolderShape: "multirepo",
    };
    mockApi.projects.createFromFolder.mockResolvedValue({
      project,
      repos: [],
      warnings: ["web has uncommitted changes — finished tasks cannot land in it until it is clean."],
    });
    const onCreated = vi.fn();
    renderWithPermissions(<CreateProjectFromFolder onCreated={onCreated} />);
    await pickAcme();

    fireEvent.click(await screen.findByRole("button", { name: "create" }));

    expect(await screen.findByText(/web has uncommitted changes/)).toBeInTheDocument();
    expect(onCreated).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "openProject" }));
    expect(onCreated).toHaveBeenCalledWith(project);
  });

  it("shows a failed create in plain language and stays on the form", async () => {
    mockApi.projects.createFromFolder.mockRejectedValue(new Error("422"));
    const onCreated = vi.fn();
    renderWithPermissions(<CreateProjectFromFolder onCreated={onCreated} />);
    await pickAcme();

    fireEvent.click(await screen.findByRole("button", { name: "create" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("createFailed");
    expect(onCreated).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "create" })).not.toBeDisabled();
  });
});
