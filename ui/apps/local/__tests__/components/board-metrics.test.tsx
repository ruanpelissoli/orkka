import { describe, it, expect, vi } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";

import { BoardMetricsStrip } from "@/components/board/BoardMetricsStrip";
import { LaneSummaryCard } from "@/components/board/LaneSummaryCard";
import { BOARD_FILTERS } from "@/components/board/board-filters";
import { FilterChip } from "@/components/ui/FilterChip";
import { SegmentedControl } from "@/components/ui/SegmentedControl";
import type { BoardLaneMetric, BoardMetricsResponse, TaskSummary } from "@/lib/types";

const NO_DELTA = { previous: null, changePct: null, trend: "Unknown" as const };

function metrics(over: Partial<BoardMetricsResponse["current"]> = {}): BoardMetricsResponse {
  return {
    window: "7d",
    from: "2026-08-20T00:00:00Z",
    to: "2026-08-27T00:00:00Z",
    previousFrom: "2026-08-13T00:00:00Z",
    current: {
      shipped: 4,
      agentSecondsPerShippedTask: 320,
      avgRunSeconds: 80,
      agentSpend: 12.5,
      wastedSpend: 1.25,
      tokens: 120_000,
      runs: 20,
      completed: 17,
      failed: 2,
      cancelled: 1,
      running: 0,
      tasksTouched: 9,
      failureRate: 0.15,
      ...over,
    },
    deltas: {
      shipped: NO_DELTA,
      agentSecondsPerShippedTask: NO_DELTA,
      avgRunSeconds: NO_DELTA,
      agentSpend: NO_DELTA,
      wastedSpend: NO_DELTA,
      failureRate: NO_DELTA,
    },
    lanes: {},
  };
}

function task(over: Partial<TaskSummary> = {}): TaskSummary {
  return {
    id: "t1",
    number: 1,
    code: "ORK-1",
    title: "A task",
    priority: "medium",
    type: "feature",
    status: "InProgress",
    currentLane: "Developing",
    branchName: null,
    prUrl: null,
    queuePosition: null,
    tags: [],
    repoId: "r1",
    repoName: "web",
    repoColor: null,
    assigneeId: null,
    assigneeName: null,
    assigneeAvatarUrl: null,
    isBlocked: false,
    blockedBy: [],
    mergeFailureReason: null,
    createdAt: "2026-08-26T00:00:00Z",
    ...over,
  } as TaskSummary;
}

/**
 * The numbers above the board. These pin the two states that would otherwise read as a broken
 * widget — no history, and a project where cost is structurally zero.
 */
describe("BoardMetricsStrip", () => {
  it("says nothing has happened rather than rendering four dashes", () => {
    const { container } = render(
      <BoardMetricsStrip metrics={metrics({ runs: 0, shipped: 0, failureRate: null })} />
    );

    expect(screen.getByText("empty")).toBeInTheDocument();
    // Four dashes in a row is what "broken" looks like; the empty copy replaces the whole strip.
    expect(container.textContent).not.toContain("failureRate");
  });

  it("shows tokens instead of money when spend is structurally zero", () => {
    // Any org on a subscription, and every runner run, is priced at $0 by design.
    render(<BoardMetricsStrip metrics={metrics({ agentSpend: 0, tokens: 120_000 })} />);

    expect(screen.getByText("tokens")).toBeInTheDocument();
    expect(screen.queryByText("agentSpend")).not.toBeInTheDocument();
  });

  it("shows agent spend, labelled as agent spend, when there is a cost", () => {
    render(<BoardMetricsStrip metrics={metrics()} />);

    expect(screen.getByText("agentSpend")).toBeInTheDocument();
    expect(screen.queryByText("tokens")).not.toBeInTheDocument();
  });

  it("renders no trend pill for an unknown trend", () => {
    // A young project should look young, not look like something failed to load.
    const { container } = render(<BoardMetricsStrip metrics={metrics()} />);
    expect(container.textContent).not.toContain("% ");
    expect(container.querySelectorAll("span.rounded-full").length).toBeGreaterThan(0);
  });

  it("colours the trend from the API verdict, not from the sign of the change", () => {
    const m = metrics();
    // A failure rate that fell is Improving even though the number went down — only the API
    // knows which direction is good for a given metric.
    m.deltas.failureRate = { previous: 30, changePct: -50, trend: "Improving" };
    const { container } = render(<BoardMetricsStrip metrics={m} />);

    const pill = Array.from(container.querySelectorAll("span")).find((el) =>
      el.textContent?.includes("-50%")
    );
    expect(pill).toBeDefined();
    expect(pill!.className).toContain("emerald");
  });
});

function laneMetric(over: Partial<BoardLaneMetric> = {}): BoardLaneMetric {
  return {
    avgAgentSeconds: 300,
    runs: 6,
    tasks: 2,
    totalAgentSeconds: 600,
    tokens: 2_400_000,
    failureRate: 0.25,
    finishedRuns: 8,
    ...over,
  };
}

/**
 * The per-lane cost card. Every column but the two human ones carries one, so these pin the cases
 * where showing a number would be a lie.
 */
describe("LaneSummaryCard", () => {
  it("reports the lane's own totals, not the board's", () => {
    render(<LaneSummaryCard metric={laneMetric()} />);

    expect(screen.getByTestId("lane-summary")).toBeInTheDocument();
    expect(screen.getByText("2.4M")).toBeInTheDocument();
    expect(screen.getByText("10m 00s")).toBeInTheDocument();
    expect(screen.getByText("25%")).toBeInTheDocument();
    expect(screen.getByText("8")).toBeInTheDocument();
  });

  it("renders nothing for a lane no agent runs in", () => {
    // Drafting and Ready never receive an execution, so the metric is simply absent — a card of
    // zeroes there would read as "instant and clean" rather than "not a thing agents do here".
    const { container } = render(<LaneSummaryCard metric={undefined} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("renders nothing while the lane's only run is still in flight", () => {
    const { container } = render(
      <LaneSummaryCard metric={laneMetric({ finishedRuns: 0, tokens: 0 })} />
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("dashes the failure rate rather than claiming 0% before anything finishes", () => {
    render(<LaneSummaryCard metric={laneMetric({ failureRate: null, tokens: 1_000 })} />);

    expect(screen.getByText("—")).toBeInTheDocument();
    expect(screen.queryByText("0%")).not.toBeInTheDocument();
  });

  it("colours a healthy lane green and a failing one red", () => {
    const { container: good } = render(<LaneSummaryCard metric={laneMetric({ failureRate: 0 })} />);
    expect(good.querySelector(".text-emerald-400")).toBeInTheDocument();

    const { container: bad } = render(<LaneSummaryCard metric={laneMetric({ failureRate: 0.6 })} />);
    expect(bad.querySelector(".text-red-400")).toBeInTheDocument();
  });
});

describe("board filters", () => {
  it("counts each condition off the task's own fields", () => {
    const tasks = [
      task({ id: "a", status: "WaitingAnswer" }),
      task({ id: "b", status: "WaitingPlanApproval" }),
      task({ id: "c", isBlocked: true }),
      task({ id: "d", mergeFailureReason: "conflicts" }),
      task({ id: "e", status: "OutOfTokens" }),
      task({ id: "f", currentLane: "Developing" }),
    ];

    const counts = Object.fromEntries(
      BOARD_FILTERS.map((f) => [f.key, tasks.filter(f.match).length])
    );

    expect(counts.needsYou).toBe(2);
    expect(counts.blocked).toBe(1);
    expect(counts.mergeFailed).toBe(1);
    expect(counts.stalled).toBe(1);
    // Every fixture task sits in Developing unless overridden, so this counts them all.
    expect(counts.agentWorking).toBe(6);
  });

  it("treats a task blocked by a dependency as blocked", () => {
    const blocked = task({ blockedBy: [{ id: "x", code: "ORK-9", title: "dep" }] as never });
    expect(BOARD_FILTERS.find((f) => f.key === "blocked")!.match(blocked)).toBe(true);
  });
});

describe("FilterChip", () => {
  it("is an independent toggle, not a radio", () => {
    const onToggle = vi.fn();
    render(<FilterChip label="Blocked" count={3} active={false} onToggle={onToggle} />);

    const chip = screen.getByRole("button", { name: /Blocked/ });
    expect(chip).toHaveAttribute("aria-pressed", "false");
    fireEvent.click(chip);
    expect(onToggle).toHaveBeenCalledOnce();
  });

  it("disables rather than hides a chip with nothing to show", () => {
    // A row that reflows as work moves through the board is a row you cannot aim at.
    render(<FilterChip label="Blocked" count={0} active={false} onToggle={vi.fn()} />);

    const chip = screen.getByRole("button", { name: /Blocked/ });
    expect(chip).toBeDisabled();
    expect(chip).toBeInTheDocument();
  });

  it("stays clickable at zero while it is the active filter", () => {
    // Otherwise turning a filter on that then empties the board traps you in it.
    render(<FilterChip label="Blocked" count={0} active onToggle={vi.fn()} />);
    expect(screen.getByRole("button", { name: /Blocked/ })).not.toBeDisabled();
  });
});

describe("SegmentedControl", () => {
  const options = [
    { value: "24h", label: "24h" },
    { value: "7d", label: "7d" },
  ];

  it("is a radiogroup — exactly one option is always chosen", () => {
    render(
      <SegmentedControl options={options} value="7d" onChange={vi.fn()} aria-label="Window" />
    );

    expect(screen.getByRole("radiogroup", { name: "Window" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "7d" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "24h" })).toHaveAttribute("aria-checked", "false");
  });

  it("reports the selected value", () => {
    const onChange = vi.fn();
    render(
      <SegmentedControl options={options} value="7d" onChange={onChange} aria-label="Window" />
    );

    fireEvent.click(screen.getByRole("radio", { name: "24h" }));
    expect(onChange).toHaveBeenCalledWith("24h");
  });
});
