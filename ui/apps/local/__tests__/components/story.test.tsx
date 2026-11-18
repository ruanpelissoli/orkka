import { describe, it, expect } from "vitest";
import { render, screen, within } from "@testing-library/react";
import { DndContext } from "@dnd-kit/core";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import "../mocks/api";
import type { TaskGroup, TaskSummary } from "@/lib/types";

import { TaskCard } from "@/components/board/TaskCard";
import { StoryPanel } from "@/components/task/StoryPanel";

function card(overrides: Partial<TaskSummary> = {}): TaskSummary {
  return {
    id: "task-2",
    number: 12,
    code: "SHOP-12",
    title: "[SHOP-12] Pay endpoint",
    priority: "medium",
    type: "Feature",
    status: "Active",
    currentLane: "Developing",
    branchName: "feature/SHOP-10-checkout",
    prUrl: null,
    queuePosition: null,
    tags: [],
    repoId: "repo-1",
    repoName: "api",
    repoColor: null,
    isBlocked: false,
    blockedBy: [],
    mergeFailureReason: null,
    createdAt: "2026-11-17T10:00:00Z",
    externalKey: "SHOP-12",
    externalUrl: "https://acme.atlassian.net/browse/SHOP-12",
    groupId: "01JSTORYGROUP000000000000",
    storyKey: "SHOP-10",
    storyOrder: 2,
    storyCount: 3,
    ...overrides,
  } as TaskSummary;
}

function story(overrides: Partial<TaskGroup> = {}): TaskGroup {
  const member = (id: string, number: number, order: number, isMerged: boolean, lane = "Drafting") => ({
    id,
    number,
    code: `SHOP-${number}`,
    title: `[SHOP-${number}] Part ${order}`,
    repoId: "repo-1",
    repoName: "api",
    repoColor: null,
    currentLane: lane,
    status: "Draft",
    prUrl: null,
    isMerged,
    prClosed: false,
    mergeFailureReason: null,
    storyOrder: order,
    externalKey: `SHOP-${number}`,
  });
  return {
    id: "01JSTORYGROUP000000000000",
    title: "[SHOP-10] Checkout",
    request: null,
    contract: null,
    contractSource: "Declared",
    mergePolicy: "Independent",
    status: "Active",
    milestoneId: null,
    ready: false,
    blockedBy: null,
    settledAt: null,
    createdAt: "2026-11-17T10:00:00Z",
    // Out of order on purpose: the panel orders by story order.
    members: [member("t3", 13, 3, false), member("t1", 11, 1, true, "Done"), member("t2", 12, 2, false, "Developing")],
    repoRequests: [],
    kind: "Story",
    branchName: "feature/SHOP-10-checkout",
    externalKey: "SHOP-10",
    externalUrl: "https://acme.atlassian.net/browse/SHOP-10",
    readyAt: null,
    branches: [
      {
        repoId: "repo-1",
        repoName: "api",
        branchName: "feature/SHOP-10-checkout",
        prUrl: "https://github.com/acme/api/pull/7",
        prState: "draft",
        readyAt: null,
      },
    ],
    ...overrides,
  } as TaskGroup;
}

describe("Stories", () => {
  it("puts a story chip with the story key and the sub-task's place on the board card", () => {
    render(
      <DndContext>
        <TaskCard task={card()} />
      </DndContext>
    );

    const chip = screen.getByTestId("story-chip");
    expect(chip).toHaveTextContent("taskGroup.storyChip");
    expect(chip).toHaveAttribute("title", "taskGroup.storyChipTitle");
    // A story member is not labelled as a cross-repo half.
    expect(screen.queryByText("taskGroup.crossRepo")).toBeNull();
  });

  it("keeps the cross-repo marker for a cross-repo half", () => {
    render(
      <DndContext>
        <TaskCard task={card({ storyKey: null, storyOrder: null, storyCount: null })} />
      </DndContext>
    );

    expect(screen.queryByTestId("story-chip")).toBeNull();
    expect(screen.getByText("taskGroup.crossRepo")).toBeInTheDocument();
  });

  it("lists the sub-tasks in order with landed / this one / pending, the shared branch and the draft PR", () => {
    render(<StoryPanel group={story()} projectId="shop" currentTaskId="t2" />);

    const rows = screen.getAllByTestId("story-member");
    expect(rows.map((r) => r.getAttribute("data-state"))).toEqual(["landed", "current", "pending"]);
    expect(within(rows[0]).getByText("✓")).toBeInTheDocument();
    expect(within(rows[1]).getByText("▶")).toBeInTheDocument();
    expect(within(rows[2]).getByText("○")).toBeInTheDocument();
    expect(within(rows[1]).getByRole("link")).toHaveAttribute("href", "/p/shop/tasks/12");

    expect(screen.getByTestId("story-branch")).toHaveTextContent("feature/SHOP-10-checkout");
    const pr = screen.getByTestId("story-pr");
    expect(within(pr).getByRole("link")).toHaveAttribute("href", "https://github.com/acme/api/pull/7");
    expect(pr).toHaveTextContent("prDraft");
    expect(screen.getByRole("link", { name: "SHOP-10" })).toHaveAttribute(
      "href",
      "https://acme.atlassian.net/browse/SHOP-10"
    );
  });

  it("says when the pull request is ready, and that it opens after the first sub-task before that", () => {
    const { unmount } = render(
      <StoryPanel
        group={story({ branches: [{ ...story().branches![0], prState: "ready", readyAt: "2026-11-17T12:00:00Z" }] })}
        projectId="shop"
      />
    );
    expect(screen.getByTestId("story-pr")).toHaveTextContent("prReady");
    unmount();

    render(
      <StoryPanel
        group={story({ branches: [{ ...story().branches![0], prUrl: null, prState: "none" }] })}
        projectId="shop"
      />
    );
    expect(screen.getByTestId("story-pr")).toHaveTextContent("storyNoPr");
  });
});
