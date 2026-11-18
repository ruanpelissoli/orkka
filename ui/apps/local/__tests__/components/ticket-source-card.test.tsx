import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { ownerMe, memberMe } from "../render-with-permissions";
import type {
  OrgMe,
  ProjectRepo,
  ProjectTrackerConfig,
  TrackerConnection,
  TrackerDefaults,
  TrackerQueryRequest,
} from "@/lib/types";
import { TRACKER_STATUS_STEPS } from "@/lib/tracker";

import { TicketSourceCard } from "@/components/project/TicketSourceCard";

const connection: TrackerConnection = {
  id: "conn-1",
  key: "jira-acme",
  kind: "jira",
  siteUrl: "https://acme.atlassian.net",
  accountId: "acc-1",
  displayName: "Ruan",
  healthy: true,
  healthDetail: null,
  machineName: "DESKTOP-RUAN",
  lastSeenAt: "2026-10-26T10:00:00Z",
  checkedAt: "2026-10-26T10:00:00Z",
};

const repos = [
  { id: "repo-api", name: "api" },
  { id: "repo-web", name: "web" },
] as ProjectRepo[];

function config(overrides: Partial<ProjectTrackerConfig> = {}): ProjectTrackerConfig {
  return {
    id: "cfg-1",
    projectId: "proj-1",
    ownerUserId: "local_user",
    trackerConnectionId: "conn-1",
    connection,
    scope: "ACME",
    extraClause: null,
    includeDescendants: false,
    autoSubmit: true,
    pollIntervalSeconds: 90,
    statusMap: {
      pickedUp: null,
      planning: null,
      developing: "In Progress",
      inReview: null,
      prOpened: null,
      merged: "Done",
    },
    branchTemplate: "{type}/{key}-{slug}",
    enabled: true,
    lastPolledAt: null,
    lastPollError: null,
    epics: [{ epicKey: "ACME-1", epicTitle: "Checkout", repoId: "repo-api", subpath: "packages\\api/" }],
    scopeMode: "Epics",
    intakeStatuses: ["To Do"],
    routingRules: [],
    defaultRepoId: null,
    storyMode: 1,
    ...overrides,
  };
}

const STATUSES = [
  { name: "Backlog", category: "todo" },
  { name: "To Do", category: "todo" },
  { name: "Selected", category: "todo" },
  { name: "In Progress", category: "inprogress" },
  { name: "Code Review", category: "inprogress" },
  { name: "Done", category: "done" },
];

const DEFAULTS: TrackerDefaults = {
  statusMap: { developing: "In Progress", prOpened: "Code Review", merged: null },
  intakeStatuses: ["Selected"],
};

/** Answers each tracker query kind the way a connected runner would. */
function runnerAnswers(
  overrides: Partial<Record<TrackerQueryRequest["kind"], unknown>> = {},
  defaults: TrackerDefaults | null = DEFAULTS
) {
  mockApi.trackers.query.mockImplementation((_id: string, req: TrackerQueryRequest) => {
    const answers: Record<string, unknown> = {
      scopes: { ok: true, scopes: [{ key: "ACME", name: "Acme" }, { key: "OPS", name: "Ops" }] },
      boards: {
        ok: true,
        boards:
          req.scope === "OPS"
            ? [{ id: "77", name: "Ops board", type: "kanban" }]
            : [
                { id: "12", name: "Web board", type: "scrum" },
                { id: "13", name: "API board", type: "kanban" },
              ],
      },
      statuses: { ok: true, statuses: STATUSES },
      epics: { ok: true, epics: [{ key: "ACME-7", title: "Search", status: "To Do", url: null }] },
      preview: {
        ok: true,
        matchCount: 2,
        sample: [
          { key: "ACME-11", title: "Add search box", type: "Story", status: "To Do", url: "https://acme.atlassian.net/browse/ACME-11" },
        ],
        effectiveQuery: 'project = ACME AND "Epic Link" in (ACME-1)',
      },
      test: { ok: true, connection },
      ...overrides,
    };
    return Promise.resolve({
      supported: true,
      result: answers[req.kind],
      defaults: req.kind === "statuses" ? defaults : undefined,
    });
  });
}

function show(props: Partial<React.ComponentProps<typeof TicketSourceCard>> = {}, me: OrgMe = ownerMe) {
  return render(
    <PermissionsProvider me={me}>
      <TicketSourceCard
        projectId="proj-1"
        initialConfig={config()}
        connections={[connection]}
        repos={repos}
        viewerUserId="local_user"
        {...props}
      />
    </PermissionsProvider>
  );
}

const statusSelect = (step: string) => screen.getByTestId(`ticket-source-status-${step}`);

async function statusesLoaded() {
  // Wait until a live status (not only a saved value) is offered.
  await waitFor(() =>
    expect(within(statusSelect("pickedUp")).getByRole("option", { name: "Backlog" })).toBeInTheDocument()
  );
}

describe("TicketSourceCard", () => {
  beforeEach(() => {
    mockApi.trackers.query.mockReset();
    mockApi.trackers.saveProjectConfig.mockReset();
    mockApi.trackers.deleteProjectConfig.mockReset();
    runnerAnswers();
  });

  it("points to Settings → Trackers when the viewer has no connections", () => {
    show({ connections: [], initialConfig: null });

    const empty = screen.getByTestId("ticket-source-no-connections");
    expect(within(empty).getByText("noConnections")).toBeInTheDocument();
    expect(within(empty).getByRole("link", { name: "addConnectionLink" })).toHaveAttribute(
      "href",
      expect.stringContaining("/settings/trackers")
    );
    expect(within(empty).getByText(/\.\/orkka tracker add jira/)).toBeInTheDocument();
    expect(mockApi.trackers.query).not.toHaveBeenCalled();
  });

  it("explains an offline runner instead of reporting an error", async () => {
    mockApi.trackers.query.mockResolvedValue({ supported: false });
    show();

    expect(await screen.findByTestId("ticket-source-offline")).toHaveTextContent("runnerOffline");
    expect(screen.queryByTestId("ticket-source-query-error")).toBeNull();
  });

  it("shows the tracker's own error when a query fails", async () => {
    runnerAnswers({ scopes: { ok: false, error: "401 Unauthorized" } });
    show();

    expect(await screen.findByTestId("ticket-source-query-error")).toBeInTheDocument();
  });

  describe("ticket status sync", () => {
    it("has one row per Orkka step, in pipeline order, each with its explanation", () => {
      show();

      const sync = screen.getByTestId("ticket-source-status-sync");
      const rows = within(sync).getAllByTestId(/^ticket-source-status-row-/);
      expect(rows.map((r) => r.dataset.testid)).toEqual(
        ["pickedUp", "planning", "developing", "inReview", "prOpened", "merged"].map(
          (s) => `ticket-source-status-row-${s}`
        )
      );
      for (const step of TRACKER_STATUS_STEPS) {
        const row = within(sync).getByTestId(`ticket-source-status-row-${step}`);
        expect(within(row).getByText(`statusStep.${step}`)).toBeInTheDocument();
        expect(within(row).getByText(`statusStepHint.${step}`)).toBeInTheDocument();
        expect(within(row).getByLabelText(`statusStep.${step}`)).toBe(statusSelect(step));
      }
      expect(within(sync).getByText("statusMapNote")).toBeInTheDocument();
    });

    it("offers the project's live statuses plus Don't change on every step", async () => {
      show();
      await statusesLoaded();

      for (const step of TRACKER_STATUS_STEPS) {
        const options = within(statusSelect(step))
          .getAllByRole("option")
          .map((o) => o.textContent);
        expect(options).toEqual(["dontChange", ...STATUSES.map((s) => s.name)]);
      }
    });

    it("pre-fills a new ticket source's status map and intake from the defaults", async () => {
      show({ initialConfig: null });

      // A new source has no scope yet; picking one loads the statuses and their defaults.
      await waitFor(() =>
        expect(within(screen.getByTestId("ticket-source-scope")).getByRole("option", { name: "Acme (ACME)" })).toBeInTheDocument()
      );
      fireEvent.change(screen.getByTestId("ticket-source-scope"), { target: { value: "ACME" } });

      await waitFor(() => expect(statusSelect("developing")).toHaveValue("In Progress"));
      expect(statusSelect("prOpened")).toHaveValue("Code Review");
      expect(statusSelect("pickedUp")).toHaveValue("");
      expect(statusSelect("planning")).toHaveValue("");
      expect(statusSelect("inReview")).toHaveValue("");
      expect(statusSelect("merged")).toHaveValue("");

      const intake = screen.getByTestId("ticket-source-intake");
      expect(within(intake).getByRole("button", { name: "Selected" })).toHaveAttribute("aria-pressed", "true");
      expect(within(intake).getByRole("button", { name: "To Do" })).toHaveAttribute("aria-pressed", "false");
    });

    it("fills only an empty Developing / PR-opened step on a saved source", async () => {
      show({
        initialConfig: config({
          statusMap: { pickedUp: "Selected", developing: null, prOpened: null, merged: "Done" },
          intakeStatuses: ["Backlog"],
        }),
      });

      await waitFor(() => expect(statusSelect("developing")).toHaveValue("In Progress"));
      expect(statusSelect("prOpened")).toHaveValue("Code Review");
      expect(statusSelect("pickedUp")).toHaveValue("Selected");
      expect(statusSelect("merged")).toHaveValue("Done");
      // The saved intake is kept.
      const intake = screen.getByTestId("ticket-source-intake");
      expect(within(intake).getByRole("button", { name: "Backlog" })).toHaveAttribute("aria-pressed", "true");
      expect(within(intake).getByRole("button", { name: "Selected" })).toHaveAttribute("aria-pressed", "false");
    });

    it("saves all six steps, Don't change as null", async () => {
      mockApi.trackers.saveProjectConfig.mockResolvedValue(config());
      runnerAnswers({}, null);
      show();
      await statusesLoaded();

      fireEvent.change(statusSelect("pickedUp"), { target: { value: "Selected" } });
      fireEvent.change(statusSelect("planning"), { target: { value: "In Progress" } });
      fireEvent.change(statusSelect("inReview"), { target: { value: "Code Review" } });
      fireEvent.change(statusSelect("merged"), { target: { value: "" } });
      fireEvent.click(screen.getByRole("button", { name: "save" }));

      await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalled());
      expect(mockApi.trackers.saveProjectConfig.mock.calls[0][1].statusMap).toEqual({
        pickedUp: "Selected",
        planning: "In Progress",
        developing: "In Progress",
        inReview: "Code Review",
        prOpened: null,
        merged: null,
      });
    });
  });

  it("saves the whole configuration in the server's shape", async () => {
    mockApi.trackers.saveProjectConfig.mockResolvedValue(config());
    show();

    await waitFor(() =>
      expect(mockApi.trackers.query).toHaveBeenCalledWith("conn-1", { kind: "statuses", scope: "ACME" }, "")
    );
    await waitFor(() => expect(statusSelect("prOpened")).toHaveValue("Code Review"));
    fireEvent.click(screen.getByRole("button", { name: "save" }));

    await waitFor(() =>
      expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalledWith(
        "proj-1",
        {
          trackerConnectionId: "conn-1",
          scope: "ACME",
          scopeMode: "Epics",
          epics: [{ epicKey: "ACME-1", epicTitle: "Checkout", repoId: "repo-api", subpath: "packages/api" }],
          routingRules: [],
          intakeStatuses: ["To Do"],
          defaultRepoId: null,
          storyMode: 1,
          boardId: null,
          boardName: null,
          extraClause: null,
          includeDescendants: false,
          autoSubmit: true,
          pollIntervalSeconds: 90,
          statusMap: {
            pickedUp: null,
            planning: null,
            developing: "In Progress",
            inReview: null,
            prOpened: "Code Review",
            merged: "Done",
          },
          branchTemplate: "{type}/{key}-{slug}",
          enabled: true,
        },
        ""
      )
    );
    expect(await screen.findByTestId("ticket-source-notice")).toHaveTextContent("saved");
  });

  it("gets a new source from nothing to savable with the defaults: connection, project, done", async () => {
    mockApi.trackers.saveProjectConfig.mockResolvedValue(config());
    show({ initialConfig: null, repos: [repos[0]] });

    await waitFor(() =>
      expect(within(screen.getByTestId("ticket-source-scope")).getByRole("option", { name: "Acme (ACME)" })).toBeInTheDocument()
    );
    fireEvent.change(screen.getByTestId("ticket-source-scope"), { target: { value: "ACME" } });
    await waitFor(() => expect(statusSelect("developing")).toHaveValue("In Progress"));

    expect(screen.queryByTestId("ticket-source-problems")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "save" }));

    await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalled());
    const body = mockApi.trackers.saveProjectConfig.mock.calls[0][1];
    expect(body.scopeMode).toBe("All");
    expect(body.epics).toEqual([]);
    expect(body.intakeStatuses).toEqual(["Selected"]);
    // A single-repo project routes everything there by default.
    expect(body.defaultRepoId).toBe("repo-api");
    expect(body.storyMode).toBe(1);
  });

  it("requires an epic only in Only-these-epics mode", () => {
    show({ initialConfig: config({ epics: [], scopeMode: "All" }) });

    expect(screen.getByRole("button", { name: "save" })).toBeEnabled();

    fireEvent.click(screen.getByTestId("ticket-source-mode-epics"));

    expect(screen.getByTestId("ticket-source-problems")).toHaveTextContent("errors.noEpics");
    expect(screen.getByRole("button", { name: "save" })).toBeDisabled();
  });

  it("keeps an epic without a repository (it only scopes intake)", async () => {
    mockApi.trackers.saveProjectConfig.mockResolvedValue(config());
    show();

    fireEvent.change(screen.getByLabelText("repoColumn"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "save" }));

    await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalled());
    expect(mockApi.trackers.saveProjectConfig.mock.calls[0][1].epics).toEqual([
      { epicKey: "ACME-1", epicTitle: "Checkout", repoId: null, subpath: null },
    ]);
  });

  it("edits, reorders and removes routing rules, and explains the fallback", async () => {
    mockApi.trackers.saveProjectConfig.mockResolvedValue(config());
    show();

    expect(screen.getByTestId("ticket-source-routing-hint")).toHaveTextContent("routingHint");

    fireEvent.click(screen.getByRole("button", { name: "ruleAdd" }));
    fireEvent.click(screen.getByRole("button", { name: "ruleAdd" }));
    const values = screen.getAllByLabelText("ruleValueLabel");
    fireEvent.change(values[0], { target: { value: "Backend" } });
    fireEvent.change(values[1], { target: { value: "frontend" } });
    fireEvent.change(screen.getAllByLabelText("ruleKindLabel")[1], { target: { value: "label" } });
    fireEvent.change(screen.getAllByLabelText("ruleRepoLabel")[1], { target: { value: "repo-web" } });

    // Move the label rule above the component rule.
    fireEvent.click(screen.getAllByRole("button", { name: "ruleUp" })[1]);

    fireEvent.click(screen.getByRole("button", { name: "save" }));
    await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalled());
    expect(mockApi.trackers.saveProjectConfig.mock.calls[0][1].routingRules).toEqual([
      { kind: "label", value: "frontend", repoId: "repo-web", subpath: null },
      { kind: "component", value: "Backend", repoId: "repo-api", subpath: null },
    ]);

    fireEvent.click(screen.getAllByRole("button", { name: "ruleRemove" })[0]);
    expect(screen.getAllByTestId("ticket-source-rule")).toHaveLength(1);
  });

  it("refuses a routing rule without a value", () => {
    show();

    fireEvent.click(screen.getByRole("button", { name: "ruleAdd" }));

    expect(screen.getByTestId("ticket-source-problems")).toHaveTextContent("errors.rule");
    expect(screen.getByRole("button", { name: "save" })).toBeDisabled();
  });

  it("stores the story-mode toggle", async () => {
    mockApi.trackers.saveProjectConfig.mockResolvedValue(config());
    show();

    fireEvent.click(within(screen.getByTestId("ticket-source-story-mode")).getByRole("switch"));
    fireEvent.click(screen.getByRole("button", { name: "save" }));

    await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalled());
    expect(mockApi.trackers.saveProjectConfig.mock.calls[0][1].storyMode).toBe(0);
  });

  it("renders the preview: count, linked sample tickets and the query that ran", async () => {
    show();

    fireEvent.click(screen.getByRole("button", { name: "preview" }));

    const preview = await screen.findByTestId("ticket-source-preview");
    expect(within(preview).getByText("previewCount")).toBeInTheDocument();
    expect(within(preview).getByRole("link", { name: "ACME-11" })).toHaveAttribute(
      "href",
      "https://acme.atlassian.net/browse/ACME-11"
    );
    expect(within(preview).getByText('project = ACME AND "Epic Link" in (ACME-1)')).toBeInTheDocument();
    expect(mockApi.trackers.query).toHaveBeenCalledWith(
      "conn-1",
      {
        kind: "preview",
        scope: "ACME",
        scopeMode: "Epics",
        intakeStatuses: ["To Do"],
        epicKeys: ["ACME-1"],
        extraClause: undefined,
        includeDescendants: false,
      },
      ""
    );
  });

  describe("board", () => {
    const boardSelect = () => screen.getByTestId("ticket-source-board");

    it("loads the scope's boards, with the whole project as the empty choice", async () => {
      show();

      await waitFor(() =>
        expect(within(boardSelect()).getByRole("option", { name: "Web board" })).toBeInTheDocument()
      );
      expect(mockApi.trackers.query).toHaveBeenCalledWith("conn-1", { kind: "boards", scope: "ACME" }, "");
      const options = within(boardSelect()).getAllByRole("option");
      expect(options.map((o) => o.textContent)).toEqual(["boardWhole", "Web board", "API board"]);
      expect(options[0]).toHaveValue("");
      expect(boardSelect()).toHaveValue("");
      expect(screen.getByText("boardHint")).toBeInTheDocument();
    });

    it("names an Azure DevOps board after its team", async () => {
      const ado = { ...connection, id: "conn-ado", kind: "ado", siteUrl: "https://dev.azure.com/contoso" };
      show({ connections: [ado], initialConfig: config({ trackerConnectionId: "conn-ado", connection: ado }) });

      await waitFor(() =>
        expect(within(boardSelect()).getAllByRole("option", { name: "boardOptionAdo" })).toHaveLength(2)
      );
    });

    it("saves and previews the picked board, and saves no board for the whole project", async () => {
      mockApi.trackers.saveProjectConfig.mockResolvedValue(config());
      show();
      await waitFor(() =>
        expect(within(boardSelect()).getByRole("option", { name: "API board" })).toBeInTheDocument()
      );

      fireEvent.change(boardSelect(), { target: { value: "13" } });
      fireEvent.click(screen.getByRole("button", { name: "preview" }));
      await waitFor(() =>
        expect(mockApi.trackers.query).toHaveBeenCalledWith(
          "conn-1",
          expect.objectContaining({ kind: "preview", boardId: "13", boardName: "API board" }),
          ""
        )
      );
      fireEvent.click(screen.getByRole("button", { name: "save" }));
      await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalledTimes(1));
      expect(mockApi.trackers.saveProjectConfig.mock.calls[0][1]).toMatchObject({ boardId: "13", boardName: "API board" });

      fireEvent.change(boardSelect(), { target: { value: "" } });
      fireEvent.click(screen.getByRole("button", { name: "save" }));
      await waitFor(() => expect(mockApi.trackers.saveProjectConfig).toHaveBeenCalledTimes(2));
      expect(mockApi.trackers.saveProjectConfig.mock.calls[1][1]).toMatchObject({ boardId: null, boardName: null });
    });

    it("keeps a saved board selectable before the list loads", () => {
      mockApi.trackers.query.mockReturnValue(new Promise(() => {}));
      show({ initialConfig: config({ boardId: "12", boardName: "Web board" }) });

      expect(boardSelect()).toHaveValue("12");
      expect(within(boardSelect()).getByRole("option", { name: "Web board" })).toBeInTheDocument();
    });

    it("is cleared, and reloaded, when the project changes", async () => {
      show({ initialConfig: config({ boardId: "12", boardName: "Web board" }) });
      await waitFor(() =>
        expect(within(boardSelect()).getByRole("option", { name: "API board" })).toBeInTheDocument()
      );
      expect(boardSelect()).toHaveValue("12");

      fireEvent.change(screen.getByTestId("ticket-source-scope"), { target: { value: "OPS" } });

      expect(boardSelect()).toHaveValue("");
      await waitFor(() =>
        expect(within(boardSelect()).getByRole("option", { name: "Ops board" })).toBeInTheDocument()
      );
      expect(within(boardSelect()).queryByRole("option", { name: "Web board" })).toBeNull();
      expect(mockApi.trackers.query).toHaveBeenCalledWith("conn-1", { kind: "boards", scope: "OPS" }, "");
    });

    it("is disabled until a project is picked", () => {
      show({ initialConfig: null });

      expect(boardSelect()).toBeDisabled();
    });
  });

  it("previews all assigned tickets without epic keys in All mode", async () => {
    show({ initialConfig: config({ scopeMode: "All" }) });

    fireEvent.click(screen.getByRole("button", { name: "preview" }));

    await screen.findByTestId("ticket-source-preview");
    expect(mockApi.trackers.query).toHaveBeenCalledWith(
      "conn-1",
      expect.objectContaining({ kind: "preview", scopeMode: "All", epicKeys: undefined }),
      ""
    );
  });

  it.each(["../outside", "/abs/path", "C:\\code", "~/code"])(
    "refuses the subfolder %j before saving",
    (bad) => {
      show();

      fireEvent.change(screen.getByLabelText("subpathColumn"), { target: { value: bad } });

      expect(screen.getAllByText("errors.subpath").length).toBeGreaterThan(0);
      expect(screen.getByRole("button", { name: "save" })).toBeDisabled();
    }
  );

  it("adds an epic from the search results without a repository", async () => {
    show();

    fireEvent.change(screen.getByTestId("ticket-source-epic-search"), { target: { value: "sea" } });
    fireEvent.click(await screen.findByText("Search"));

    const rows = screen.getByTestId("ticket-source-epics");
    expect(within(rows).getByText("ACME-7")).toBeInTheDocument();
    expect(screen.getAllByLabelText("repoColumn")[1]).toHaveValue("");
  });

  it("refuses a branch template without {key}", () => {
    show();

    fireEvent.change(screen.getByLabelText("branchLabel"), { target: { value: "feature/{slug}" } });

    expect(screen.getByRole("button", { name: "save" })).toBeDisabled();
  });

  it("says whose ticket source it is when the viewer doesn't own it", () => {
    show({ initialConfig: config({ ownerUserId: "user_other" }), ownerName: "Ana" });

    expect(screen.getByTestId("ticket-source-owner")).toHaveTextContent("ownedBy");
  });

  it("shows when the source has not been polled yet", () => {
    show();

    expect(screen.getByTestId("ticket-source-poll")).toHaveTextContent("notPolledYet");
  });

  it("removes the ticket source after confirming", async () => {
    mockApi.trackers.deleteProjectConfig.mockResolvedValue(undefined);
    show();

    fireEvent.click(screen.getByRole("button", { name: "remove" }));
    fireEvent.click(screen.getByRole("button", { name: "remove" }));

    await waitFor(() =>
      expect(mockApi.trackers.deleteProjectConfig).toHaveBeenCalledWith("proj-1", "")
    );
  });

  it("is read-only without the Manage projects permission", () => {
    show({}, memberMe(["TasksCreate"]));

    expect(screen.getByText("readOnly")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "save" })).toBeNull();
  });
});
