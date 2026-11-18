import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { ownerMe } from "../render-with-permissions";

import { AddRepoByUrlPanel } from "@/app/[locale]/(dashboard)/p/[projectId]/repos/AddRepoByUrlPanel";
import { RepoAccessCheckButton } from "@/app/[locale]/(dashboard)/p/[projectId]/repos/RepoAccessCheckButton";

function show() {
  return render(
    <PermissionsProvider me={ownerMe}>
      <AddRepoByUrlPanel projectId="proj-1" />
    </PermissionsProvider>
  );
}

function typeUrl(value: string) {
  fireEvent.change(screen.getByLabelText("urlLabel"), { target: { value } });
}

describe("Add repository by URL", () => {
  beforeEach(() => {
    mockApi.repos.add.mockReset();
    mockApi.repos.add.mockResolvedValue({ id: "repo-1" });
  });

  it("classifies the URL as it is typed and prefills the name", () => {
    show();

    typeUrl("https://github.com/acme/web-app.git");

    expect(screen.getByTestId("repo-url-kind").textContent).toContain("kind.https");
    expect(screen.getByTestId("repo-url-kind").textContent).toContain("github.com");
    expect(screen.getByLabelText("nameLabel")).toHaveValue("web-app");
    expect(screen.getByTestId("repo-forge-select")).toHaveValue("github");
  });

  it("adds the repo with the detected forge left for the server to detect", async () => {
    show();

    typeUrl("git@github.com:acme/api.git");
    fireEvent.change(screen.getByLabelText("cliAccountLabel"), { target: { value: "ruan-work" } });
    fireEvent.click(screen.getByRole("button", { name: "add" }));

    await waitFor(() =>
      expect(mockApi.repos.add).toHaveBeenCalledWith(
        "proj-1",
        {
          gitUrl: "git@github.com:acme/api.git",
          name: "api",
          defaultBranch: "main",
          provider: null,
          cliAccount: "ruan-work",
        },
        ""
      )
    );
  });

  it("sends the forge override for an ssh host alias", async () => {
    show();

    typeUrl("git@gitlab-work:group/svc.git");
    // A host alias can't be read, so the hint asks the user to pick the forge.
    expect(screen.getByText("forgeAliasHint")).toBeInTheDocument();
    fireEvent.change(screen.getByTestId("repo-forge-select"), { target: { value: "gitlab" } });
    fireEvent.click(screen.getByRole("button", { name: "add" }));

    await waitFor(() =>
      expect(mockApi.repos.add).toHaveBeenCalledWith(
        "proj-1",
        expect.objectContaining({ gitUrl: "git@gitlab-work:group/svc.git", provider: "gitlab" }),
        ""
      )
    );
  });

  it("refuses a URL with a token in it before it reaches the server", () => {
    show();

    typeUrl("https://ghp_secret@github.com/acme/api.git");

    expect(screen.getByText("credentialInUrl")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "add" })).toBeDisabled();
  });

  it("points a folder path at the local-folder flow instead", () => {
    show();

    typeUrl("C:\\code\\api");

    expect(screen.getByText("localPath")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "add" })).toBeDisabled();
  });

  it("shows the server's refusal", async () => {
    mockApi.repos.add.mockRejectedValue(new Error("Repository URL is not reachable."));
    show();

    typeUrl("https://dev.azure.com/org/proj/_git/repo");
    fireEvent.click(screen.getByRole("button", { name: "add" }));

    expect(await screen.findByText("Repository URL is not reachable.")).toBeInTheDocument();
  });
});

describe("Check access from my runner", () => {
  beforeEach(() => {
    mockApi.repos.accessCheck.mockReset();
  });

  it("lists what the runner could and could not read, with the advice", async () => {
    mockApi.repos.accessCheck.mockResolvedValue({
      supported: true,
      repos: [
        { repoId: "r1", name: "api", ok: true, reason: null, advice: null },
        {
          repoId: "r2",
          name: "web",
          ok: false,
          reason: "Permission denied (publickey).",
          advice: "Add your ssh key to the forge.",
        },
      ],
    });
    render(<RepoAccessCheckButton projectId="proj-1" />);

    fireEvent.click(screen.getByRole("button", { name: "check" }));

    expect(await screen.findByText("ok")).toBeInTheDocument();
    expect(screen.getByText("notOk")).toBeInTheDocument();
    expect(
      screen.getByText("Permission denied (publickey). Add your ssh key to the forge.")
    ).toBeInTheDocument();
    expect(mockApi.repos.accessCheck).toHaveBeenCalledWith("proj-1", "");
  });

  it("says the runner could not answer rather than reporting a failure", async () => {
    mockApi.repos.accessCheck.mockResolvedValue({ supported: false, repos: [] });
    render(<RepoAccessCheckButton projectId="proj-1" />);

    fireEvent.click(screen.getByRole("button", { name: "check" }));

    expect(await screen.findByText("unsupported")).toBeInTheDocument();
  });
});
