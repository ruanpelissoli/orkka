import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { ownerMe } from "../render-with-permissions";
import type {
  ProjectRepo,
  ProjectTrackerConfig,
  TrackerConnection,
  TrackerDefaults,
  TrackerQueryRequest,
} from "@/lib/types";

import { TicketSourceCard } from "@/components/project/TicketSourceCard";

const github: TrackerConnection = {
  id: "conn-gh",
  key: "github-github-com",
  kind: "github",
  siteUrl: "https://github.com",
  accountId: "octocat",
  displayName: "octocat",
  healthy: true,
  healthDetail: null,
  machineName: "DESKTOP-RUAN",
  lastSeenAt: "2026-11-19T10:00:00Z",
  checkedAt: "2026-11-19T10:00:00Z",
  authMode: "gh",
};

const repos = [
  { id: "repo-api", name: "api" },
  { id: "repo-web", name: "web" },
] as ProjectRepo[];

const OPTIONS = ["Todo", "In Progress", "In Review", "Done"];

const DEFAULTS: TrackerDefaults = {
  statusMap: { developing: "In Progress", inReview: "In Review", merged: "Done" },
  intakeStatuses: ["Todo"],
};

/** Answers the queries the way a runner with a GitHub connection would. */
function runnerAnswers() {
  mockApi.trackers.query.mockImplementation((_id: string, req: TrackerQueryRequest) => {
    const answers: Record<string, unknown> = {
      scopes: { ok: true, scopes: [{ key: "octocat", name: "octocat" }, { key: "acme", name: "Acme Inc" }] },
      boards: {
        ok: true,
        boards: [
          { id: "PVT_1", name: "Roadmap", number: 5, url: "https://github.com/orgs/acme/projects/5" },
          { id: "PVT_2", name: "Bugs", number: 7 },
        ],
      },
      fields: {
        ok: true,
        fields: [
          { id: "F_status", name: "Status", options: OPTIONS },
          { id: "F_stage", name: "Stage", options: ["Queued", "Building", "Shipped"] },
        ],
      },
      repos: { ok: true, repositories: ["acme/api", "acme/web"] },
      statuses: {
        ok: true,
        statuses: (req.statusField === "Stage" ? ["Queued", "Building", "Shipped"] : OPTIONS).map((name) => ({
          name,
          category: null,
        })),
      },
      preview: { ok: true, matchCount: 1, sample: [{ key: "acme/api#12", title: "Fix login", type: "Issue", status: "Todo", url: null }] },
      test: { ok: true, connection: github },
    };
    return Promise.resolve({
      supported: true,
      result: answers[req.kind],
      defaults: req.kind === "statuses" ? DEFAULTS : undefined,
    });
  });
}

function show(initialConfig: ProjectTrackerConfig | null = null) {
  return render(
    <PermissionsProvider me={ownerMe}>
      <TicketSourceCard
        projectId="proj-1"
        initialConfig={initialConfig}
        connections={[github]}
        repos={repos}
        viewerUserId="local_user"
      />
    </PermissionsProvider>
  );
}

const calls = (kind: TrackerQueryRequest["kind"]) =>
  mockApi.trackers.query.mock.calls.filter((c) => (c[1] as TrackerQueryRequest).kind === kind).map((c) => c[1]);

async function pickOwnerAndProject() {
  await waitFor(() =>
    expect(within(screen.getByTestId("ticket-source-scope")).getByRole("option", { name: "Acme Inc (acme)" })).toBeInTheDocument()
  );
  fireEvent.change(screen.getByTestId("ticket-source-scope"), { target: { value: "acme" } });
  await waitFor(() =>
    expect(within(screen.getByTestId("ticket-source-board")).getAllByRole("option", { name: "projectOption" })).toHaveLength(2)
  );
  fireEvent.change(screen.getByTestId("ticket-source-board"), { target: { value: "PVT_1" } });
  await waitFor(() =>
    expect(within(screen.getByTestId("ticket-source-repo-filter")).getByRole("button", { name: "acme/api" })).toBeInTheDocument()
  );
}

describe("TicketSourceCard — GitHub", () => {
  beforeEach(() => {
    mockApi.trackers.query.mockReset();
    mockApi.trackers.saveProjectConfig.mockReset();
    runnerAnswers();
  });

  it("names the owner and requires a project — there is no whole-scope option", async () => {
    show();

    expect(screen.getByText("scopeLabel.github")).toBeInTheDocument();
    const board = screen.getByTestId("ticket-source-board");
    expect(within(board).getByRole("option", { name: "projectPlaceholder" })).toHaveValue("");
    expect(within(board).queryByRole("option", { name: "boardWhole" })).toBeNull();
    // Nothing GitHub has no use for.
    expect(screen.queryByTestId("ticket-source-mode-epics")).toBeNull();
    expect(screen.queryByTestId("ticket-source-epic-search")).toBeNull();
    expect(screen.getByTestId("ticket-source-github-intake-hint")).toHaveTextContent("githubIntakeHint");
    expect(screen.getByTestId("ticket-source-routing-hint")).toHaveTextContent("routingHintGithub");

    await waitFor(() => expect(calls("scopes")).toHaveLength(1));
    fireEvent.change(screen.getByTestId("ticket-source-scope"), { target: { value: "acme" } });
    expect(await screen.findByText("errors.project")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "save" })).toBeDisabled();
    // No statuses before a project is picked: they live in its field.
    expect(calls("statuses")).toHaveLength(0);
  });

  it("asks for the project's fields, repositories and the status field's options", async () => {
    show();
    await pickOwnerAndProject();

    expect(calls("boards")).toContainEqual({ kind: "boards", scope: "acme" });
    expect(calls("fields")).toContainEqual({ kind: "fields", scope: "acme", boardId: "PVT_1", boardName: "Roadmap" });
    expect(calls("repos")).toContainEqual({ kind: "repos", scope: "acme", boardId: "PVT_1", boardName: "Roadmap" });
    await waitFor(() =>
      expect(calls("statuses")).toContainEqual({ kind: "statuses", scope: "acme", boardId: "PVT_1", statusField: "Status" })
    );
    // The defaults fill the map and the intake.
    await waitFor(() => expect(screen.getByTestId("ticket-source-status-developing")).toHaveValue("In Progress"));
    expect(screen.getByTestId("ticket-source-status-inReview")).toHaveValue("In Review");
    expect(screen.getByTestId("ticket-source-status-merged")).toHaveValue("Done");
    expect(within(screen.getByTestId("ticket-source-intake")).getByRole("button", { name: "Todo" })).toHaveAttribute(
      "aria-pressed",
      "true"
    );

    // Another single-select field brings its own options.
    fireEvent.change(screen.getByTestId("ticket-source-status-field"), { target: { value: "Stage" } });
    await waitFor(() =>
      expect(calls("statuses")).toContainEqual({ kind: "statuses", scope: "acme", boardId: "PVT_1", statusField: "Stage" })
    );
    await waitFor(() =>
      expect(within(screen.getByTestId("ticket-source-status-pickedUp")).getByRole("option", { name: "Shipped" })).toBeInTheDocument()
    );
  });

  it("previews with the project, field and repository filter", async () => {
    show();
    await pickOwnerAndProject();
    fireEvent.click(within(screen.getByTestId("ticket-source-repo-filter")).getByRole("button", { name: "acme/api" }));
    await waitFor(() => expect(screen.getByTestId("ticket-source-status-developing")).toHaveValue("In Progress"));

    fireEvent.click(screen.getByRole("button", { name: "preview" }));

    await screen.findByTestId("ticket-source-preview");
    expect(calls("preview")).toContainEqual({
      kind: "preview",
      scope: "acme",
      scopeMode: "All",
      intakeStatuses: ["Todo"],
      boardId: "PVT_1",
      boardName: "Roadmap",
      statusField: "Status",
      repoFilter: ["acme/api"],
    });
  });

  it("saves the owner, project, status field, repository filter and the status map", async () => {
    mockApi.trackers.saveProjectConfig.mockImplementation(() => Promise.resolve({}));
    show();
    await pickOwnerAndProject();
    fireEvent.click(within(screen.getByTestId("ticket-source-repo-filter")).getByRole("button", { name: "acme/web" }));
    await waitFor(() => expect(screen.getByTestId("ticket-source-status-developing")).toHaveValue("In Progress"));
    fireEvent.change(screen.getByTestId("ticket-source-status-pickedUp"), { target: { value: "" } });

    fireEvent.click(screen.getByRole("button", { name: "save" }));

    await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalled());
    const body = mockApi.trackers.saveProjectConfig.mock.calls[0][1];
    expect(body).toMatchObject({
      trackerConnectionId: "conn-gh",
      scope: "acme",
      scopeMode: "All",
      epics: [],
      boardId: "PVT_1",
      boardName: "Roadmap",
      statusField: "Status",
      repoFilter: ["acme/web"],
      intakeStatuses: ["Todo"],
      extraClause: null,
      includeDescendants: false,
    });
    expect(body.statusMap).toEqual({
      pickedUp: null,
      planning: null,
      developing: "In Progress",
      inReview: "In Review",
      prOpened: null,
      merged: "Done",
    });
  });

  it("keeps a saved source's field and filter while the runner answers", async () => {
    show({
      id: "cfg-1",
      projectId: "proj-1",
      ownerUserId: "local_user",
      trackerConnectionId: "conn-gh",
      connection: github,
      scope: "acme",
      extraClause: null,
      includeDescendants: false,
      autoSubmit: true,
      pollIntervalSeconds: 90,
      statusMap: { developing: "Building" },
      branchTemplate: "{type}/{key}-{slug}",
      enabled: true,
      lastPolledAt: null,
      lastPollError: null,
      epics: [],
      scopeMode: "All",
      intakeStatuses: ["Queued"],
      routingRules: [],
      defaultRepoId: null,
      storyMode: 1,
      boardId: "PVT_1",
      boardName: "Roadmap",
      statusField: "Stage",
      repoFilter: ["acme/legacy"],
    });

    expect(screen.getByTestId("ticket-source-status-field")).toHaveValue("Stage");
    expect(
      within(screen.getByTestId("ticket-source-repo-filter")).getByRole("button", { name: "acme/legacy" })
    ).toHaveAttribute("aria-pressed", "true");
    await waitFor(() =>
      expect(calls("statuses")).toContainEqual({ kind: "statuses", scope: "acme", boardId: "PVT_1", statusField: "Stage" })
    );
    // A saved map is not overwritten by the defaults.
    expect(screen.getByTestId("ticket-source-status-developing")).toHaveValue("Building");
  });
});
