import { describe, it, expect, beforeEach } from "vitest";
import { screen, fireEvent, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";
import { ToastProvider } from "@/components/ui";
import type { BoardStory, TaskBoardResponse, TaskSummary } from "@/lib/types";

import { TaskBoard } from "@/components/board/TaskBoard";
import { buildStorySwimlanes, storyPrState, NO_STORY_SWIMLANE } from "@/components/board/story-swimlanes";

function task(
  id: string,
  title: string,
  story?: { groupId: string; key: string; order: number },
  lane: TaskSummary["currentLane"] = "Drafting"
): TaskSummary {
  return {
    id,
    number: Number(id.replace(/\D/g, "")) || 1,
    code: `SHOP-${id}`,
    title,
    priority: "medium",
    type: "Feature",
    status: "Draft",
    currentLane: lane,
    branchName: null,
    prUrl: null,
    queuePosition: null,
    tags: [],
    repoId: "repo-1",
    repoName: null,
    repoColor: null,
    isBlocked: false,
    blockedBy: [],
    mergeFailureReason: null,
    createdAt: "2026-11-20T10:00:00Z",
    externalKey: `K-${id}`,
    externalUrl: null,
    groupId: story?.groupId ?? null,
    storyKey: story?.key ?? null,
    storyOrder: story?.order ?? null,
    storyCount: story ? 2 : null,
  } as unknown as TaskSummary;
}

const checkout: BoardStory = {
  groupId: "g-checkout",
  projectId: "proj-1",
  key: "SHOP-10",
  title: "Checkout flow",
  url: "https://acme.atlassian.net/browse/SHOP-10",
  branchName: "feature/SHOP-10-checkout",
  memberCount: 2,
  doneCount: 1,
  readyAt: null,
  status: "Active",
  pullRequests: [{ repoId: "repo-1", url: "https://github.com/acme/shop/pull/41", draft: true, state: "open" }],
};

const inCheckout = (order: number) => ({ groupId: "g-checkout", key: "SHOP-10", order });

const board: TaskBoardResponse = {
  lanes: [
    {
      key: "Drafting",
      label: "Drafting",
      order: 0,
      // Board order differs from story order on purpose: the swimlane sorts by story order.
      tasks: [task("12", "Pay button", inCheckout(2)), task("7", "Board-made chore")],
    },
    { key: "Developing", label: "Developing", order: 1, tasks: [task("11", "Cart summary", inCheckout(1), "Developing")] },
  ],
  stories: [checkout],
};

function show(data: TaskBoardResponse = board) {
  return renderWithPermissions(
    <ToastProvider>
      <TaskBoard boardData={data} projectId="proj-1" projectUlid="proj-1" />
    </ToastProvider>
  );
}

describe("buildStorySwimlanes", () => {
  it("puts each story's cards in its own row in story order, then the rest", () => {
    const rows = buildStorySwimlanes(
      [{ key: "Drafting", label: "Drafting", order: 0, tasks: [task("12", "b", inCheckout(2)), task("11", "a", inCheckout(1)), task("7", "c")] }],
      [checkout]
    );

    expect(rows.map((r) => r.id)).toEqual(["g-checkout", NO_STORY_SWIMLANE]);
    expect(rows[0].lanes[0].tasks.map((t) => t.id)).toEqual(["11", "12"]);
    expect(rows[1].lanes[0].tasks.map((t) => t.id)).toEqual(["7"]);
  });

  it("keeps a story first seen in a live card, and drops rows the filters emptied", () => {
    const rows = buildStorySwimlanes(
      [{ key: "Drafting", label: "Drafting", order: 0, tasks: [task("21", "x", { groupId: "g-new", key: "SHOP-20", order: 1 })] }],
      [checkout]
    );

    expect(rows.map((r) => r.id)).toEqual(["g-new"]);
    expect(rows[0].storyKey).toBe("SHOP-20");
    expect(rows[0].story).toBeNull();
  });

  it("names the furthest pull request state", () => {
    expect(storyPrState(null)).toBe("none");
    expect(storyPrState(checkout)).toBe("draft");
    expect(storyPrState({ ...checkout, readyAt: "2026-11-20T12:00:00Z", pullRequests: [{ ...checkout.pullRequests[0], draft: false }] })).toBe("ready");
    expect(storyPrState({ ...checkout, pullRequests: [{ ...checkout.pullRequests[0], state: "merged" }] })).toBe("merged");
  });
});

describe("Board grouped by story", () => {
  beforeEach(() => window.localStorage.clear());

  it("offers the grouping only when the board has stories", () => {
    show({ lanes: [{ key: "Drafting", label: "Drafting", order: 0, tasks: [task("7", "Board-made chore")] }] });
    expect(screen.queryByTestId("board-group-by")).toBeNull();
  });

  it("shows one swimlane per story with its progress and PR, and the rest last", () => {
    show();
    fireEvent.change(screen.getByTestId("board-group-by"), { target: { value: "story" } });

    const lanes = screen.getAllByTestId("board-swimlane");
    expect(lanes.map((l) => l.getAttribute("data-swimlane"))).toEqual(["g-checkout", NO_STORY_SWIMLANE]);
    const story = within(lanes[0]);
    expect(story.getByText("SHOP-10")).toBeInTheDocument();
    expect(story.getByText("Checkout flow")).toBeInTheDocument();
    expect(story.getByTestId("swimlane-count").textContent).toBe("storyProgress");
    expect(story.getByTestId("swimlane-pr").getAttribute("href")).toBe("https://github.com/acme/shop/pull/41");
    expect(story.getByText("Cart summary")).toBeInTheDocument();
    expect(story.queryByTestId("story-chip")).toBeNull();
    expect(within(lanes[1]).getByText("Board-made chore")).toBeInTheDocument();
  });

  it("remembers the grouping and collapsed stories for this board", () => {
    const first = show();
    fireEvent.change(screen.getByTestId("board-group-by"), { target: { value: "story" } });
    fireEvent.click(within(screen.getAllByTestId("board-swimlane")[0]).getByTestId("swimlane-toggle"));
    expect(screen.queryByText("Cart summary")).toBeNull();
    first.unmount();

    show();
    expect((screen.getByTestId("board-group-by") as HTMLSelectElement).value).toBe("story");
    expect(screen.queryByText("Cart summary")).toBeNull();
    expect(screen.getByText("Board-made chore")).toBeInTheDocument();
  });

  it("notes a tracker blocker Orkka does not work", () => {
    show({
      lanes: [{ key: "Drafting", label: "Drafting", order: 0, tasks: [{ ...task("7", "Chore"), externalBlockedBy: "OPS-9" }] }],
    });
    expect(screen.getByTestId("external-blocked-by").textContent).toBe("taskCard.externalBlockedBy");
  });
});
