import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, fireEvent, waitFor, render } from "@testing-library/react";
import { DndContext } from "@dnd-kit/core";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderWithPermissions } from "../render-with-permissions";
import { ToastProvider } from "@/components/ui";
import { useBoardSignalR } from "@/hooks/use-board-signalr";
import type { BoardProjectRef, TaskBoardResponse, TaskSummary } from "@/lib/types";

// The detail modal loads a whole task; here it only reports which task and project it was given.
vi.mock("@/components/board/TaskDetailModal", () => ({
  TaskDetailModal: (props: { taskId: string | null; projectId: string }) =>
    props.taskId ? `Modal:${props.taskId}:${props.projectId}` : null,
}));

import { TaskBoard } from "@/components/board/TaskBoard";
import { LaneColumn } from "@/components/board/LaneColumn";

const SHOP: BoardProjectRef = { id: "01SHOP", key: "SHOP", name: "Shop Front", slug: "shop", hasTesting: false };
const BILL: BoardProjectRef = { id: "01BILL", key: "BILL", name: "Billing API", slug: "billing", hasTesting: true };

function task(id: string, number: number, title: string, project: BoardProjectRef, lane = "Drafting"): TaskSummary {
  return {
    id,
    number,
    code: `${project.key}-${number}`,
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
    assigneeId: null,
    assigneeName: null,
    assigneeAvatarUrl: null,
    isBlocked: false,
    blockedBy: [],
    mergeFailureReason: null,
    createdAt: "2026-11-19T10:00:00Z",
    project,
  } as TaskSummary;
}

const board: TaskBoardResponse = {
  lanes: [
    {
      key: "drafting",
      label: "Drafting",
      order: 0,
      // Both projects have a task number 1 — only the code tells them apart.
      tasks: [task("t-shop-1", 1, "Checkout card", SHOP), task("t-bill-1", 1, "Invoice export", BILL)],
    },
  ],
};

function show(project: string | null = null) {
  return renderWithPermissions(
    <ToastProvider>
      <TaskBoard
        boardData={board}
        projectId={project ?? ""}
        projectUlid=""
        workspace={{ project, projectUlids: project ? [project === "shop" ? SHOP.id : BILL.id] : [SHOP.id, BILL.id] }}
      />
    </ToastProvider>
  );
}

describe("TaskBoard on the workspace board", () => {
  beforeEach(() => {
    setRoute({});
    vi.mocked(useBoardSignalR).mockClear();
    mockApi.board.metrics.mockClear();
    mockApi.board.searchWorkspace.mockClear();
    mockApi.board.search.mockClear();
  });

  it("badges every card with its project when showing every project", () => {
    show();
    const badges = screen.getAllByTestId("task-card-project").map((b) => b.textContent);
    expect(badges).toEqual(["Shop Front", "Billing API"]);
  });

  it("drops the project badge once narrowed to one project", () => {
    show("shop");
    expect(screen.queryByTestId("task-card-project")).toBeNull();
  });

  it("listens to the board group of every project it shows, and has no project metrics", () => {
    show();
    const calls = vi.mocked(useBoardSignalR).mock.calls.map(([options]) => options);
    expect(calls.some((o) => o.projectIds?.join(",") === "01SHOP,01BILL" && o.projectId === undefined)).toBe(true);
    // The metrics strip is per project: its hook joins nothing and fetches nothing.
    expect(calls.some((o) => o.projectId === "" || (o.projectId && o.projectIds === undefined))).toBe(false);
    expect(mockApi.board.metrics).not.toHaveBeenCalled();
  });

  it("names a task by its code in ?task=, and opens it in its own project", async () => {
    setRoute({}, "task=BILL-1");
    const { container } = show();
    expect(container.textContent).toContain("Modal:t-bill-1:billing");
  });

  it("puts the code, not the number, in ?task= when a card is clicked", () => {
    window.history.replaceState(null, "", "/en/board");
    show();
    fireEvent.click(screen.getByText("Invoice export"));
    expect(new URL(window.location.href).searchParams.get("task")).toBe("BILL-1");
  });

  it("searches the workspace (or the narrowed project) rather than one project", async () => {
    show("billing");
    fireEvent.change(screen.getByPlaceholderText("boardSearch.placeholder"), { target: { value: "invoice" } });
    await waitFor(() =>
      expect(mockApi.board.searchWorkspace).toHaveBeenCalledWith(
        "billing",
        "invoice",
        { includeMessages: false, includeArchived: false },
        ""
      )
    );
    expect(mockApi.board.search).not.toHaveBeenCalled();
  });
});

describe("LaneColumn drop targets", () => {
  function lane(allowsTesting: boolean) {
    return render(
      <DndContext>
        <LaneColumn
          laneKey="Testing"
          label="Testing"
          tasks={[]}
          activeDragLane="CodeReview"
          activeDragAllowsTesting={allowsTesting}
        />
      </DndContext>
    );
  }

  it("is a backward drop target for a card whose project tests", () => {
    const { container } = lane(true);
    expect(container.querySelector(".opacity-40")).toBeNull();
  });

  it("refuses a card whose project has no Testing lane", () => {
    const { container } = lane(false);
    expect(container.querySelector(".opacity-40")).not.toBeNull();
  });
});
