import { describe, it, expect } from "vitest";
import { fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { renderWithPermissions } from "../render-with-permissions";
import { mockApi } from "../mocks/api";
import { ActivityPage, ExecutionTimeline } from "@/components/activity";
import { ToastProvider } from "@/components/ui";
import type {
  ActivitySummary,
  ActivityTaskRow,
  ProjectExecutionRow,
  ProjectExecutionsResponse,
} from "@/lib/types";

function row(overrides: Partial<ProjectExecutionRow> = {}): ProjectExecutionRow {
  return {
    executionId: "e1",
    taskId: "t1",
    taskKey: "ORK-1",
    taskTitle: "Add login",
    agentId: "a1",
    agentName: "Pixel",
    agentRole: "FrontendDev",
    lane: "Developing",
    status: "Completed",
    startedAt: "2026-08-17T01:00:00Z",
    completedAt: "2026-08-17T01:10:00Z",
    durationSeconds: 600,
    tokensUsed: 1000,
    cost: 0.5,
    errorMessage: null,
    ...overrides,
  };
}

function task(overrides: Partial<ActivityTaskRow> = {}): ActivityTaskRow {
  return {
    taskId: "t1",
    taskKey: "ORK-1",
    taskTitle: "Add login",
    currentLane: "Developing",
    status: "Active",
    prUrl: null,
    isMerged: false,
    prClosedAt: null,
    mergeFailureReason: null,
    mergeFailureKind: null,
    setupFailure: null,
    reviewCycleCount: 0,
    qaCycleCount: 0,
    checkCycleCount: 0,
    baseSyncCycleCount: 0,
    hasAgentWorking: false,
    lastActivityAt: "2026-08-17T01:10:00Z",
    ...overrides,
  };
}

function summary(overrides: Partial<ActivitySummary> = {}): ActivitySummary {
  return {
    runs: 3,
    running: 1,
    completed: 1,
    failed: 1,
    cancelled: 0,
    tasksTouched: 2,
    durationSeconds: 1230,
    cost: 1.25,
    wastedCost: 0.25,
    tokens: 42_000,
    agents: [{ agentId: "a1", cost: 1.25, tokens: 42_000 }],
    ...overrides,
  };
}

const initial: ProjectExecutionsResponse = {
  from: "2026-08-17T00:00:00Z",
  to: "2026-08-17T06:00:00Z",
  truncated: false,
  executions: [
    row({}),
    row({ executionId: "e2", status: "Failed", errorMessage: "tests are red", lane: "Testing" }),
    row({
      executionId: "e3",
      taskId: "t2",
      taskKey: "ORK-2",
      taskTitle: "Fix nav",
      status: "Running",
      completedAt: null,
      durationSeconds: null,
    }),
  ],
  summary: summary(),
  tasks: [task(), task({ taskId: "t2", taskKey: "ORK-2", taskTitle: "Fix nav", prUrl: "https://github.com/acme/app/pull/9" })],
};

// ToastProvider mirrors the dashboard layout, which wraps every page in one — the run drawer
// reports a retry through it.
function renderPage(data: ProjectExecutionsResponse = initial) {
  return renderWithPermissions(
    <ToastProvider>
      <ActivityPage projectId="proj" projectUlid="01H" initial={data} />
    </ToastProvider>
  );
}

describe("ActivityPage", () => {
  it("lands on the digest, not the timeline", () => {
    const { container, getByTestId } = renderPage();

    // The view switch is a radiogroup — exactly one view is always chosen, which is what
    // aria-checked expresses and aria-pressed (an independent toggle) does not.
    expect(getByTestId("view-digest")).toHaveAttribute("aria-checked", "true");
    expect(getByTestId("attention-list")).toBeTruthy();
    expect(getByTestId("outcome-list")).toBeTruthy();
    expect(container.querySelectorAll('[data-testid="execution-bar"]')).toHaveLength(0);
  });

  it("puts the failure's error message on the page rather than in a tooltip", () => {
    const { getByTestId } = renderPage();
    const attention = getByTestId("attention-list");

    expect(attention.textContent).toContain("tests are red");
    expect(attention.querySelector('[data-attention-kind="failed"]')).toBeTruthy();
  });

  it("drives the totals strip from the server summary, not from the rows on screen", () => {
    // Contrived on purpose: the rows carry a failed run while the server summary reports none.
    // The translator mock echoes keys and drops values, so the discriminator is which lines
    // appear — `summaryTasks` has no row-level equivalent at all, and `summaryFailed` would be
    // there if the strip were counting what it can see.
    const { container, getByTestId } = renderPage({
      ...initial,
      truncated: true,
      summary: summary({ runs: 412, failed: 0, wastedCost: 0, tasksTouched: 37 }),
    });

    const strip = getByTestId("activity-summary").textContent ?? "";
    expect(strip).toContain("summaryTasks");
    expect(strip).not.toContain("summaryFailed");
    expect(container.textContent).toContain("truncated");
  });

  it("swaps to the timeline on demand and back", () => {
    const { container, getByTestId } = renderPage();

    fireEvent.click(getByTestId("view-timeline"));
    expect(container.querySelectorAll('[data-testid="execution-bar"]')).toHaveLength(3);
    expect(container.querySelector('[data-testid="attention-list"]')).toBeNull();

    fireEvent.click(getByTestId("view-digest"));
    expect(container.querySelectorAll('[data-testid="execution-bar"]')).toHaveLength(0);
  });

  it("reports each task's outcome and links its PR", () => {
    const { getByTestId } = renderPage();
    const outcomes = getByTestId("outcome-list");

    expect(outcomes.querySelector('[data-outcome="prOpen"]')).toBeTruthy();
    const pr = [...outcomes.querySelectorAll("a")].find(
      (a) => a.getAttribute("href") === "https://github.com/acme/app/pull/9"
    );
    expect(pr).toBeTruthy();
    expect(pr!.getAttribute("rel")).toContain("noopener");
  });

  it("breaks spend down per agent as a list-price estimate in dollars, never credits", () => {
    const { getByTestId } = renderPage();
    const table = getByTestId("spend-by-agent").textContent ?? "";

    expect(table).toContain("Pixel");
    // Runs use the developer's own subscription: the figure is an estimate, marked as one.
    expect(table).toContain("≈ $1.25");
    expect(table).not.toContain("1,250");
  });

  it("shows tokens instead of money when the window cost nothing", () => {
    const { getByTestId } = renderPage({
      ...initial,
      summary: summary({ cost: 0, wastedCost: 0, tokens: 42_000, agents: [{ agentId: "a1", cost: 0, tokens: 42_000 }] }),
    });
    // The mocked translator echoes keys, so the heading names which column is in play.
    expect(getByTestId("spend-by-agent").textContent).toContain("tokens");
  });

  it("opens the run behind an attention row — the page is no longer a dead end", async () => {
    mockApi.executions.list.mockResolvedValue([]);
    const { getByTestId } = renderPage();

    // Several rows offer a run; take the failed one's, which is the reason to click.
    const failedRow = getByTestId("attention-list").querySelector('[data-attention-kind="failed"]')!;
    fireEvent.click([...failedRow.querySelectorAll("button")].find((b) => b.textContent === "openRun")!);

    // The drawer fetches the owning task's runs to find the one that was clicked.
    await waitFor(() => expect(mockApi.executions.list).toHaveBeenCalledWith("t1", expect.anything()));
    expect(getByTestId("run-drawer")).toBeTruthy();
  });

  it("narrows the zones to a brushed range, and gives the window back on clear", () => {
    const { container, getByTestId, getByText } = renderPage();
    const strip = getByTestId("density-strip");

    // jsdom gives every element a zero-size rect, so a drag maps to the window's left edge —
    // which is exactly the interesting case: an empty slice must empty the zones, not silently
    // fall back to showing everything.
    fireEvent.pointerDown(strip, { clientX: 0, pointerId: 1 });
    fireEvent.pointerMove(strip, { clientX: 40, pointerId: 1 });
    fireEvent.pointerUp(strip, { clientX: 40, pointerId: 1 });

    expect(container.textContent).toContain("emptyRange");
    expect(container.querySelector('[data-testid="outcome-list"]')).toBeNull();

    fireEvent.click(getByText("rangeClear"));
    expect(getByTestId("outcome-list")).toBeTruthy();
    expect(container.textContent).not.toContain("emptyRange");
  });

  it("treats a click on the strip as clearing, not as a zero-width selection", () => {
    const { container, getByTestId } = renderPage();
    const strip = getByTestId("density-strip");

    fireEvent.pointerDown(strip, { clientX: 20, pointerId: 1 });
    fireEvent.pointerUp(strip, { clientX: 21, pointerId: 1 });

    expect(getByTestId("outcome-list")).toBeTruthy();
    expect(container.textContent).not.toContain("emptyRange");
  });

  it("shows the empty state when the window has no runs", () => {
    const { container } = renderPage({ ...initial, executions: [], tasks: [] });
    expect(container.textContent).toContain("empty");
    expect(container.querySelector('[data-testid="attention-list"]')).toBeNull();
  });
});

describe("ExecutionTimeline", () => {
  const from = Date.parse("2026-08-17T00:00:00Z");
  const to = Date.parse("2026-08-17T06:00:00Z");

  it("renders one row per task and one bar per execution", () => {
    const { container, getAllByTestId } = renderWithPermissions(
      <ExecutionTimeline projectId="proj" rows={initial.executions} from={from} to={to} />
    );

    expect(container.textContent).toContain("ORK-1");
    expect(container.textContent).toContain("ORK-2");
    expect(getAllByTestId("execution-bar")).toHaveLength(3);
    expect(container.querySelectorAll('[data-status="Running"]')).toHaveLength(1);
  });

  it("links each task row to the task page", () => {
    const { container } = renderWithPermissions(
      <ExecutionTimeline projectId="proj" rows={initial.executions} from={from} to={to} />
    );
    const hrefs = [...container.querySelectorAll("a")].map((a) => a.getAttribute("href"));
    expect(hrefs).toContain("/p/proj/tasks/t1");
    expect(hrefs).toContain("/p/proj/tasks/t2");
  });

  it("makes bars clickable only when there is somewhere to click through to", () => {
    // Container-scoped: both renders mount into the same document body, so a global query
    // would keep finding the first render's bar no matter which one is under test.
    const inert = renderWithPermissions(
      <ExecutionTimeline projectId="proj" rows={[row()]} from={from} to={to} />
    );
    expect(inert.container.querySelector('[data-testid="execution-bar"]')!.tagName).toBe("DIV");

    const clicked: string[] = [];
    const live = renderWithPermissions(
      <ExecutionTimeline
        projectId="proj"
        rows={[row()]}
        from={from}
        to={to}
        onSelectRun={(r) => clicked.push(r.executionId)}
      />
    );
    const bar = live.container.querySelector('[data-testid="execution-bar"]')!;
    expect(bar.tagName).toBe("BUTTON");
    fireEvent.click(bar);
    expect(clicked).toEqual(["e1"]);
  });
});
