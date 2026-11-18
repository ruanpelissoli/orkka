import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";

import { LocalFolderPickerModal } from "@/components/repos/LocalFolderPickerModal";
import { LocalRepoRemotePanel } from "@/app/[locale]/(dashboard)/p/[projectId]/repos/LocalRepoRemotePanel";

const ROOT_LISTING = {
  supported: true,
  path: "C:\\Projects",
  parentPath: null,
  entries: [
    { name: "existing-repo", fullPath: "C:\\Projects\\existing-repo", isGitRepo: true },
    { name: "just-a-folder", fullPath: "C:\\Projects\\just-a-folder", isGitRepo: false },
  ],
  error: null,
};

describe("LocalFolderPickerModal", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockApi.runnerFs.browse.mockResolvedValue(ROOT_LISTING);
    mockApi.repos.linkLocal.mockResolvedValue({
      id: "repo-1",
      name: "just-a-folder",
      path: "C:\\Projects\\just-a-folder",
      defaultBranch: "main",
    });
  });

  function render(onLinked?: () => void) {
    return renderWithPermissions(
      <LocalFolderPickerModal
        open
        onClose={() => {}}
        projectId="proj-1"
        onLinked={onLinked}
      />
    );
  }

  it("lists the runner's folders when opened", async () => {
    render();

    await waitFor(() =>
      expect(mockApi.runnerFs.browse).toHaveBeenCalledWith(null, "")
    );
    expect(await screen.findByText("existing-repo")).toBeInTheDocument();
    expect(screen.getByText("just-a-folder")).toBeInTheDocument();
  });

  // The whole point of the hosted flow: a plain folder is a valid starting point,
  // because the runner git-inits it on link. Restricting selection to existing repos
  // would put the user back in a terminal, which is what this replaces.
  it("lets a folder that is not yet a repository be selected and linked", async () => {
    render();

    fireEvent.click(await screen.findByText("just-a-folder"));
    fireEvent.click(screen.getByRole("button", { name: /linkCount/ }));

    await waitFor(() =>
      expect(mockApi.repos.linkLocal).toHaveBeenCalledWith(
        "proj-1",
        "C:\\Projects\\just-a-folder",
        ""
      )
    );
  });

  it("shows the start-your-runner callout instead of an error when nothing answers", async () => {
    mockApi.runnerFs.browse.mockResolvedValue({
      supported: false,
      path: null,
      parentPath: null,
      entries: [],
      error: null,
    });

    render();

    expect(await screen.findByText("folderPicker.runnerOffline")).toBeInTheDocument();
  });

  it("surfaces a link failure per folder without closing", async () => {
    mockApi.repos.linkLocal.mockRejectedValue(new Error("Path is not readable."));
    const onLinked = vi.fn();
    render(onLinked);

    fireEvent.click(await screen.findByText("existing-repo"));
    fireEvent.click(screen.getByRole("button", { name: /linkCount/ }));

    expect(await screen.findByText("Path is not readable.")).toBeInTheDocument();
  });

  // Pick mode chooses the ONE base folder of a new project: selecting another entry replaces
  // the choice, and with nothing selected the folder being shown is the one used.
  it("hands back a single folder in pick mode", async () => {
    const onPick = vi.fn();
    const onClose = vi.fn();
    renderWithPermissions(<LocalFolderPickerModal open onClose={onClose} onPick={onPick} />);

    fireEvent.click(await screen.findByText("existing-repo"));
    fireEvent.click(screen.getByText("just-a-folder"));
    // Creating an empty repository makes no sense for a project folder.
    expect(screen.queryByText("folderPicker.newRepo")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "folderPicker.useFolder" }));

    expect(onPick).toHaveBeenCalledWith({
      fullPath: "C:\\Projects\\just-a-folder",
      name: "just-a-folder",
    });
    expect(onClose).toHaveBeenCalled();
    expect(mockApi.repos.linkLocal).not.toHaveBeenCalled();
  });

  it("uses the folder being shown when nothing is selected in pick mode", async () => {
    const onPick = vi.fn();
    renderWithPermissions(<LocalFolderPickerModal open onClose={() => {}} onPick={onPick} />);

    await screen.findByText("existing-repo");
    fireEvent.click(screen.getByRole("button", { name: "folderPicker.useFolder" }));

    expect(onPick).toHaveBeenCalledWith({ fullPath: "C:\\Projects", name: "Projects" });
  });
});

describe("LocalRepoRemotePanel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  function render() {
    return renderWithPermissions(
      <LocalRepoRemotePanel projectId="proj-1" repoId="repo-1" repoName="app" />
    );
  }

  it("asks for the repository URL while the folder has no origin", async () => {
    mockApi.repos.syncStatus.mockResolvedValue({
      supported: true,
      isGitRepo: true,
      hasRemote: false,
      hasUpstream: false,
      branch: "main",
      ahead: 0,
      behind: 0,
      error: null,
    });
    mockApi.repos.attachRemote.mockResolvedValue({
      remoteUrl: "https://github.com/ruan/app.git",
    });

    render();

    const input = await screen.findByPlaceholderText(
      "git@github.com:you/your-repo.git"
    );
    fireEvent.change(input, {
      target: { value: "https://github.com/ruan/app" },
    });
    fireEvent.click(screen.getByRole("button", { name: "connectAndPush" }));

    await waitFor(() =>
      expect(mockApi.repos.attachRemote).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        "https://github.com/ruan/app",
        ""
      )
    );
  });

  it("offers a push once the folder has an origin and is ahead", async () => {
    mockApi.repos.syncStatus.mockResolvedValue({
      supported: true,
      isGitRepo: true,
      hasRemote: true,
      hasUpstream: true,
      branch: "main",
      ahead: 3,
      behind: 0,
      error: null,
    });

    render();

    const push = await screen.findByRole("button", { name: "push" });
    fireEvent.click(push);

    await waitFor(() =>
      expect(mockApi.repos.push).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        "main",
        ""
      )
    );
  });

  // Nothing to push means the button would only ever produce "Everything up-to-date".
  it("disables the push button when there is nothing to send", async () => {
    mockApi.repos.syncStatus.mockResolvedValue({
      supported: true,
      isGitRepo: true,
      hasRemote: true,
      hasUpstream: true,
      branch: "main",
      ahead: 0,
      behind: 0,
      error: null,
    });

    render();

    expect(await screen.findByRole("button", { name: "push" })).toBeDisabled();
  });

  it("renders nothing actionable when no runner answered", async () => {
    mockApi.repos.syncStatus.mockResolvedValue({
      supported: false,
      isGitRepo: false,
      hasRemote: false,
      hasUpstream: false,
      branch: null,
      ahead: 0,
      behind: 0,
      error: null,
    });

    render();

    expect(await screen.findByText("runnerOfflineHint")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "push" })).not.toBeInTheDocument();
  });
});
