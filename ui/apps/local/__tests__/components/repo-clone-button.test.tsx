import { describe, it, expect, beforeEach } from "vitest";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderWithPermissions, memberMe } from "../render-with-permissions";

import { RepoCloneButton } from "@/app/[locale]/(dashboard)/p/[projectId]/repos/RepoCloneButton";

function render(me?: Parameters<typeof renderWithPermissions>[1]) {
  return renderWithPermissions(
    <RepoCloneButton projectId="proj-1" repoId="repo-1" repoName="app" />,
    me
  );
}

describe("RepoCloneButton", () => {
  beforeEach(() => {
    mockApi.repos.cloneAccess.mockClear();
    mockApi.repos.cloneAccess.mockResolvedValue({
      command: "git clone https://github.com/acme/app.git",
      cloneUrl: "https://github.com/acme/app.git",
      defaultBranch: "main",
    });
  });

  it("fetches the clone command on open and shows it", async () => {
    render();

    fireEvent.click(screen.getByRole("button", { name: "cloneRepo" }));

    await waitFor(() =>
      expect(mockApi.repos.cloneAccess).toHaveBeenCalledWith(
        "proj-1",
        "repo-1",
        ""
      )
    );
    expect(
      await screen.findByText("git clone https://github.com/acme/app.git")
    ).toBeInTheDocument();
  });

  it("is hidden for members without ReposManage", () => {
    render(memberMe());

    expect(
      screen.queryByRole("button", { name: "cloneRepo" })
    ).not.toBeInTheDocument();
  });
});
