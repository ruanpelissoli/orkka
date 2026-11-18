import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, within, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderPage } from "../render-page";
import { ApiError } from "@/lib/api";
import type { TrackerConnection } from "@/lib/types";

import { JIRA_API_TOKEN_URL, TrackerConnectionsList } from "@/components/settings/TrackerConnections";
import TrackersSettingsPage from "@/app/[locale]/(dashboard)/settings/trackers/page";

const SECRET = "ATATT3xFfGF0-super-secret-token";

const jira: TrackerConnection = {
  id: "c1",
  key: "jira-acme",
  kind: "jira",
  siteUrl: "https://acme.atlassian.net",
  accountId: "acc-1",
  displayName: "Ruan",
  healthy: true,
  healthDetail: null,
  machineName: "DESKTOP-RUAN",
  lastSeenAt: "2026-10-26T10:00:00Z",
  checkedAt: null,
};

const ado: TrackerConnection = {
  ...jira,
  id: "c2",
  key: "ado-acme",
  kind: "ado",
  siteUrl: "https://dev.azure.com/acme",
  healthy: false,
  healthDetail: "The personal access token expired.",
  machineName: "WORK-LAPTOP",
};

const jiraForm = () => within(screen.getByTestId("tracker-add-jira"));
const adoForm = () => within(screen.getByTestId("tracker-add-ado"));
const tokenInput = (kind: "jira" | "ado") =>
  screen.getByTestId(`tracker-add-${kind}-token`) as HTMLInputElement;

function fillJira(token = SECRET) {
  fireEvent.change(jiraForm().getByLabelText("siteLabel"), { target: { value: "acme.atlassian.net" } });
  fireEvent.change(jiraForm().getByLabelText("emailLabel"), { target: { value: "ruan@acme.com" } });
  fireEvent.change(tokenInput("jira"), { target: { value: token } });
}

/** Every string a call to the API client was given, other than the request body itself. */
function nonBodyArgs(calls: unknown[][]): string[] {
  return calls.flatMap((args) =>
    args.filter((a): a is string => typeof a === "string")
  );
}

describe("TrackerConnectionsList", () => {
  beforeEach(() => {
    mockApi.trackers.addConnection.mockReset();
    mockApi.trackers.removeConnection.mockReset();
    mockApi.trackers.query.mockReset();
    window.localStorage.clear();
    window.sessionStorage.clear();
  });

  it("lists each connection with its kind, site, machine and health", () => {
    render(<TrackerConnectionsList connections={[jira, ado]} />);

    const j = within(screen.getByTestId("tracker-connection-jira-acme"));
    expect(j.getByText("acme.atlassian.net")).toBeInTheDocument();
    expect(j.getByText("kind.jira")).toBeInTheDocument();
    expect(j.getByText("healthy")).toBeInTheDocument();

    const a = within(screen.getByTestId("tracker-connection-ado-acme"));
    expect(a.getByText("dev.azure.com/acme")).toBeInTheDocument();
    expect(a.getByText("unhealthy")).toBeInTheDocument();
    expect(a.getByText("The personal access token expired.")).toBeInTheDocument();
    expect(
      screen.getByTestId("tracker-connection-ado-acme").querySelector('[data-tracker-icon="ado"]')
    ).not.toBeNull();
  });

  it("shows both add forms and the terminal alternative when there are none", () => {
    render(<TrackerConnectionsList connections={[]} />);

    expect(screen.getByText("none")).toBeInTheDocument();
    expect(screen.getByTestId("tracker-add-jira")).toBeInTheDocument();
    expect(screen.getByTestId("tracker-add-ado")).toBeInTheDocument();
    expect(screen.getByText(/\.\/orkka tracker add jira --site/)).toBeInTheDocument();
  });

  it("links the Jira form to Atlassian's API-token page and states the ADO scopes", () => {
    render(<TrackerConnectionsList connections={[]} />);

    expect(jiraForm().getByRole("link", { name: "tokenLink" })).toHaveAttribute("href", JIRA_API_TOKEN_URL);
    expect(JIRA_API_TOKEN_URL).toBe("https://id.atlassian.com/manage-profile/security/api-tokens");
    expect(screen.getByTestId("tracker-add-ado-scopes")).toHaveTextContent("adoScopes");
  });

  it.each(["jira", "ado"] as const)("masks the %s token: type=password, autocomplete=off", (kind) => {
    render(<TrackerConnectionsList connections={[]} />);

    const input = tokenInput(kind);
    expect(input).toHaveAttribute("type", "password");
    expect(input).toHaveAttribute("autocomplete", "off");
    // Not a form field the browser could submit into a URL before hydration.
    expect(input.closest("form")).toBeNull();
  });

  it("posts the Jira connection with the token in the body only, then adds it to the list", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({ supported: true, ok: true, connection: jira });
    render(<TrackerConnectionsList connections={[]} />);

    fillJira();
    fireEvent.click(jiraForm().getByRole("button", { name: "submit" }));

    await waitFor(() =>
      expect(mockApi.trackers.addConnection).toHaveBeenCalledWith(
        { kind: "jira", site: "acme.atlassian.net", email: "ruan@acme.com", token: SECRET },
        ""
      )
    );
    expect(await screen.findByTestId("tracker-connection-jira-acme")).toBeInTheDocument();
    expect(screen.getByTestId("tracker-add-jira-outcome")).toHaveTextContent("added");
  });

  it("never puts the token in a URL, browser storage or another call argument", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({ supported: true, ok: true, connection: jira });
    render(<TrackerConnectionsList connections={[]} />);

    fillJira();
    fireEvent.click(jiraForm().getByRole("button", { name: "submit" }));
    await waitFor(() => expect(mockApi.trackers.addConnection).toHaveBeenCalled());

    expect(window.location.href).not.toContain(SECRET);
    expect(nonBodyArgs(mockApi.trackers.addConnection.mock.calls)).not.toContain(SECRET);
    const stored = [
      ...Object.keys(window.localStorage).map((k) => window.localStorage.getItem(k)),
      ...Object.keys(window.sessionStorage).map((k) => window.sessionStorage.getItem(k)),
    ].join("\n");
    expect(stored).not.toContain(SECRET);
    expect(document.body.innerHTML).not.toContain(SECRET);
  });

  it("clears the token after a successful submit", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({ supported: true, ok: true, connection: jira });
    render(<TrackerConnectionsList connections={[]} />);

    fillJira();
    fireEvent.click(jiraForm().getByRole("button", { name: "submit" }));

    await screen.findByTestId("tracker-connection-jira-acme");
    expect(tokenInput("jira").value).toBe("");
  });

  it("explains a runner that is offline or too old, and clears the token", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({ supported: false, ok: false });
    render(<TrackerConnectionsList connections={[]} />);

    fillJira();
    fireEvent.click(jiraForm().getByRole("button", { name: "submit" }));

    expect(await screen.findByTestId("tracker-add-jira-outcome")).toHaveTextContent("runnerUnsupported");
    expect(tokenInput("jira").value).toBe("");
    expect(screen.queryByTestId("tracker-connection-jira-acme")).toBeNull();
  });

  it("shows the tracker's refusal, and clears the token", async () => {
    mockApi.trackers.addConnection.mockResolvedValue({
      supported: true,
      ok: false,
      error: "401: the API token is not valid",
    });
    render(<TrackerConnectionsList connections={[]} />);

    fireEvent.change(adoForm().getByLabelText("orgLabel"), { target: { value: "acme" } });
    fireEvent.change(tokenInput("ado"), { target: { value: SECRET } });
    fireEvent.click(adoForm().getByRole("button", { name: "submit" }));

    expect(await screen.findByTestId("tracker-add-ado-outcome")).toHaveTextContent("failed");
    expect(mockApi.trackers.addConnection).toHaveBeenCalledWith({ kind: "ado", site: "acme", token: SECRET }, "");
    expect(tokenInput("ado").value).toBe("");
  });

  it("shows a 400's ProblemDetails message, and clears the token", async () => {
    mockApi.trackers.addConnection.mockRejectedValue(
      new ApiError(400, JSON.stringify({ errors: { email: ["Give the email of your Atlassian account."] } }))
    );
    render(<TrackerConnectionsList connections={[]} />);

    fillJira();
    fireEvent.click(jiraForm().getByRole("button", { name: "submit" }));

    expect(await screen.findByTestId("tracker-add-jira-outcome")).toHaveTextContent(
      "Give the email of your Atlassian account."
    );
    expect(tokenInput("jira").value).toBe("");
  });

  it("re-tests a connection through the runner and shows the new health", async () => {
    mockApi.trackers.query.mockResolvedValue({
      supported: true,
      result: { ok: true, connection: { ...ado, healthy: true, healthDetail: null } },
    });
    render(<TrackerConnectionsList connections={[ado]} />);

    const row = within(screen.getByTestId("tracker-connection-ado-acme"));
    fireEvent.click(row.getByRole("button", { name: "retest" }));

    await waitFor(() => expect(row.getByText("healthy")).toBeInTheDocument());
    expect(mockApi.trackers.query).toHaveBeenCalledWith("c2", { kind: "test" }, "");
    expect(row.getByTestId("tracker-connection-ado-acme-outcome")).toHaveTextContent("retestOk");
  });

  it("says how to fix an offline runner on re-test", async () => {
    mockApi.trackers.query.mockResolvedValue({ supported: false });
    render(<TrackerConnectionsList connections={[jira]} />);

    fireEvent.click(screen.getByRole("button", { name: "retest" }));

    expect(await screen.findByTestId("tracker-connection-jira-acme-outcome")).toHaveTextContent(
      "add.runnerUnsupported"
    );
  });

  it("removes a connection only after confirming", async () => {
    mockApi.trackers.removeConnection.mockResolvedValue({ supported: true, ok: true });
    render(<TrackerConnectionsList connections={[jira]} />);

    fireEvent.click(screen.getByRole("button", { name: "remove" }));
    expect(mockApi.trackers.removeConnection).not.toHaveBeenCalled();
    expect(screen.getByText("removeConfirm")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "remove" }));

    await waitFor(() => expect(screen.queryByTestId("tracker-connection-jira-acme")).toBeNull());
    expect(mockApi.trackers.removeConnection).toHaveBeenCalledWith("c1", "");
    expect(screen.getByTestId("tracker-notice")).toHaveTextContent("removed");
  });

  it("keeps the connection and explains when the runner can't remove it", async () => {
    mockApi.trackers.removeConnection.mockResolvedValue({ supported: false, ok: false });
    render(<TrackerConnectionsList connections={[jira]} />);

    fireEvent.click(screen.getByRole("button", { name: "remove" }));
    fireEvent.click(screen.getByRole("button", { name: "remove" }));

    expect(await screen.findByTestId("tracker-connection-jira-acme-outcome")).toHaveTextContent(
      "add.runnerUnsupported"
    );
    expect(screen.getByTestId("tracker-connection-jira-acme")).toBeInTheDocument();
  });

  it("drops a connection that is already gone (404)", async () => {
    mockApi.trackers.removeConnection.mockRejectedValue(new ApiError(404, ""));
    render(<TrackerConnectionsList connections={[jira]} />);

    fireEvent.click(screen.getByRole("button", { name: "remove" }));
    fireEvent.click(screen.getByRole("button", { name: "remove" }));

    await waitFor(() => expect(screen.queryByTestId("tracker-connection-jira-acme")).toBeNull());
  });
});

describe("Trackers settings page", () => {
  it("renders the ticket trackers section from the caller's connections", async () => {
    mockApi.trackers.connections.mockResolvedValue([jira]);

    await renderPage(<TrackersSettingsPage />);

    expect(screen.getByTestId("tracker-section")).toHaveTextContent("sectionTitle");
    expect(screen.getByTestId("tracker-connection-jira-acme")).toBeInTheDocument();
  });

  it("falls back to the add forms when the list can't be read", async () => {
    mockApi.trackers.connections.mockRejectedValue(new Error("down"));

    await renderPage(<TrackersSettingsPage />);

    expect(screen.getByTestId("tracker-add-jira")).toBeInTheDocument();
    expect(screen.getByTestId("tracker-add-commands")).toBeInTheDocument();
  });
});
