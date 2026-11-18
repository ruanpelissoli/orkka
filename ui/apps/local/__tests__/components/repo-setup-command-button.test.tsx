import { describe, it, expect, beforeEach } from "vitest";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderWithPermissions, memberMe } from "../render-with-permissions";
import type { ProjectRepo } from "@/lib/types";

import { RepoSetupCommandButton } from "@/app/[locale]/(dashboard)/p/[projectId]/repos/RepoSetupCommandButton";

const repo: ProjectRepo = {
  id: "repo-1",
  projectId: "proj-1",
  name: "app",
  gitUrl: "https://github.com/acme/app.git",
  defaultBranch: "main",
  provider: "github",
  workspaceStatus: "ready",
  cardColor: "#6366f1",
  setupCommand: "npm ci",
  qaRunCommand: "npm run dev",
  buildCommand: "npm run build",
  checkCommand: "npm run lint && npm test",
  createdAt: "2025-01-01T00:00:00Z",
};

function render(overrides?: Partial<ProjectRepo>, me?: Parameters<typeof renderWithPermissions>[1]) {
  return renderWithPermissions(
    <RepoSetupCommandButton projectId="proj-1" repo={{ ...repo, ...overrides }} />,
    me
  );
}

describe("RepoSetupCommandButton", () => {
  beforeEach(() => {
    mockApi.repos.update.mockClear();
    mockApi.repos.update.mockResolvedValue({ ...repo });
  });

  it("opens with all four stored commands", () => {
    render();
    fireEvent.click(screen.getByRole("button", { name: "workspaceSettings" }));

    const [setup, run, build, check] = screen.getAllByRole("textbox");
    expect(setup).toHaveValue("npm ci");
    expect(run).toHaveValue("npm run dev");
    expect(build).toHaveValue("npm run build");
    expect(check).toHaveValue("npm run lint && npm test");
  });

  it("saves all four commands alongside the repo's current default branch", async () => {
    render();
    fireEvent.click(screen.getByRole("button", { name: "workspaceSettings" }));

    fireEvent.change(screen.getAllByRole("textbox")[0], {
      target: { value: "  npm ci && npm run build  " },
    });
    fireEvent.click(screen.getByText("setupSave"));

    // Trimmed, and the branch is echoed back so the PATCH doesn't reset it.
    await waitFor(() =>
      expect(mockApi.repos.update).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        {
          defaultBranch: "main",
          setupCommand: "npm ci && npm run build",
          qaRunCommand: "npm run dev",
          buildCommand: "npm run build",
          checkCommand: "npm run lint && npm test",
        },
        ""
      )
    );
  });

  it("saves a per-repo check command, run after every dev run to gate the PR", async () => {
    render({ checkCommand: null });
    fireEvent.click(screen.getByRole("button", { name: "workspaceSettings" }));

    const check = screen.getAllByRole("textbox")[3];
    expect(check).toHaveValue("");

    fireEvent.change(check, { target: { value: "  dotnet build && dotnet test  " } });
    fireEvent.click(screen.getByText("setupSave"));

    await waitFor(() =>
      expect(mockApi.repos.update).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        expect.objectContaining({ checkCommand: "dotnet build && dotnet test" }),
        ""
      )
    );
  });

  it("saves a per-repo build command, used to check a resolved merge compiles", async () => {
    render({ buildCommand: null });
    fireEvent.click(screen.getByRole("button", { name: "workspaceSettings" }));

    const build = screen.getAllByRole("textbox")[2];
    expect(build).toHaveValue("");

    fireEvent.change(build, { target: { value: "  dotnet build  " } });
    fireEvent.click(screen.getByText("setupSave"));

    await waitFor(() =>
      expect(mockApi.repos.update).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        expect.objectContaining({ buildCommand: "dotnet build" }),
        ""
      )
    );
  });

  it("saves a per-repo run command, which supersedes the project setting", async () => {
    render();
    fireEvent.click(screen.getByRole("button", { name: "workspaceSettings" }));

    fireEvent.change(screen.getAllByRole("textbox")[1], {
      target: { value: "dotnet run" },
    });
    fireEvent.click(screen.getByText("setupSave"));

    await waitFor(() =>
      expect(mockApi.repos.update).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        expect.objectContaining({ qaRunCommand: "dotnet run" }),
        ""
      )
    );
  });

  it("sends an empty string to clear a command", async () => {
    render();
    fireEvent.click(screen.getByRole("button", { name: "workspaceSettings" }));

    fireEvent.change(screen.getAllByRole("textbox")[0], { target: { value: "" } });
    fireEvent.click(screen.getByText("setupSave"));

    await waitFor(() =>
      expect(mockApi.repos.update).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        expect.objectContaining({ setupCommand: "" }),
        ""
      )
    );
  });

  it("warns against pasting secrets", () => {
    render();
    fireEvent.click(screen.getByRole("button", { name: "workspaceSettings" }));

    expect(screen.getByText("setupSecretsHint")).toBeInTheDocument();
  });

  it("is hidden for members without ReposManage", () => {
    render(undefined, memberMe());

    expect(
      screen.queryByRole("button", { name: "workspaceSettings" })
    ).not.toBeInTheDocument();
  });
});
