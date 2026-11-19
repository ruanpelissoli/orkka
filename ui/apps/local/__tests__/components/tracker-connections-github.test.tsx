import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, within, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import type { TrackerConnection } from "@/lib/types";

import { TrackerConnectionsList, gitHubTokenUrl } from "@/components/settings/TrackerConnections";

const SECRET = "ghp_super-secret-token-value";

const github: TrackerConnection = {
  id: "c3",
  key: "github-github-com",
  kind: "github",
  siteUrl: "https://github.com",
  accountId: "octocat",
  displayName: "octocat",
  healthy: true,
  healthDetail: null,
  machineName: "DESKTOP-RUAN",
  lastSeenAt: "2026-11-19T10:00:00Z",
  checkedAt: null,
  authMode: "gh",
};

const githubForm = () => within(screen.getByTestId("tracker-add-github"));

describe("TrackerConnectionsList — GitHub", () => {
  beforeEach(() => {
    mockApi.trackers.addConnection.mockReset();
    window.localStorage.clear();
    window.sessionStorage.clear();
  });

  it("defaults to the gh login on github.com and asks for no token", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({ supported: true, ok: true, connection: github });
    render(<TrackerConnectionsList connections={[]} />);

    expect(githubForm().getByLabelText("hostLabel")).toHaveValue("github.com");
    expect(screen.getByTestId("tracker-add-github-auth-gh")).toBeChecked();
    expect(screen.getByTestId("tracker-add-github-auth-token")).not.toBeChecked();
    expect(screen.queryByTestId("tracker-add-github-token")).toBeNull();
    expect(githubForm().getByText("ghStays")).toBeInTheDocument();

    fireEvent.click(githubForm().getByRole("button", { name: "submit" }));

    await waitFor(() =>
      expect(mockApi.trackers.addConnection).toHaveBeenCalledWith(
        { kind: "github", site: "github.com", token: "", authMode: "gh" },
        ""
      )
    );
    const row = await screen.findByTestId("tracker-connection-github-github-com");
    expect(within(row).getByText("kind.github")).toBeInTheDocument();
    expect(screen.getByTestId("tracker-connection-github-github-com-auth")).toHaveTextContent("authModeGh");
    expect(row.querySelector('[data-tracker-icon="github"]')).not.toBeNull();
  });

  it("posts a pasted token for a GHE.com host and clears it afterwards", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({
      supported: true,
      ok: true,
      connection: { ...github, siteUrl: "https://acme.ghe.com", authMode: "token" },
    });
    render(<TrackerConnectionsList connections={[]} />);

    fireEvent.change(githubForm().getByLabelText("hostLabel"), {
      target: { value: "https://acme.ghe.com/orgs/acme" },
    });
    fireEvent.click(screen.getByTestId("tracker-add-github-auth-token"));
    const input = screen.getByTestId("tracker-add-github-token") as HTMLInputElement;
    expect(input).toHaveAttribute("type", "password");
    expect(input).toHaveAttribute("autocomplete", "off");
    expect(screen.getByTestId("tracker-add-github-scopes")).toHaveTextContent("githubScopes");
    expect(githubForm().getByRole("link", { name: "githubTokenLink" })).toHaveAttribute(
      "href",
      "https://acme.ghe.com/settings/tokens"
    );
    // The token option with no token is not ready to submit.
    expect(githubForm().getByRole("button", { name: "submit" })).toBeDisabled();

    fireEvent.change(input, { target: { value: SECRET } });
    fireEvent.click(githubForm().getByRole("button", { name: "submit" }));

    await waitFor(() =>
      expect(mockApi.trackers.addConnection).toHaveBeenCalledWith(
        { kind: "github", site: "acme.ghe.com", token: SECRET, authMode: "token" },
        ""
      )
    );
    await screen.findByTestId("tracker-connection-github-github-com");
    expect((screen.getByTestId("tracker-add-github-token") as HTMLInputElement).value).toBe("");
    expect(screen.getByTestId("tracker-connection-github-github-com-auth")).toHaveTextContent("authModeToken");
    expect(document.body.innerHTML).not.toContain(SECRET);
    expect(window.location.href).not.toContain(SECRET);
  });

  it("refuses a GitHub Enterprise Server host", () => {
    render(<TrackerConnectionsList connections={[]} />);

    fireEvent.change(githubForm().getByLabelText("hostLabel"), { target: { value: "git.corp.example" } });

    expect(githubForm().getByText("hostInvalid")).toBeInTheDocument();
    expect(githubForm().getByRole("button", { name: "submit" })).toBeDisabled();
  });

  it("shows the runner's refusal when it is too old for GitHub", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({
      supported: true,
      ok: false,
      error: "Your runner does not know GitHub Projects yet — update Orkka (`git pull`, then `./orkka start`).",
    });
    render(<TrackerConnectionsList connections={[]} />);

    fireEvent.click(githubForm().getByRole("button", { name: "submit" }));

    expect(await screen.findByTestId("tracker-add-github-outcome")).toHaveTextContent("failed");
    expect(screen.queryByTestId("tracker-connection-github-github-com")).toBeNull();
  });

  it("offers the GitHub terminal command", () => {
    render(<TrackerConnectionsList connections={[]} />);

    expect(screen.getByText(/\.\/orkka tracker add github/)).toBeInTheDocument();
  });

  it("points the token link at the host's own settings", () => {
    expect(gitHubTokenUrl("github.com")).toBe("https://github.com/settings/tokens");
    expect(gitHubTokenUrl("acme.ghe.com")).toBe("https://acme.ghe.com/settings/tokens");
    expect(gitHubTokenUrl("not a host")).toBe("https://github.com/settings/tokens");
  });
});
