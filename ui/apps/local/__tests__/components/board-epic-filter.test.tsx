import { describe, it, expect } from "vitest";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";
import { ToastProvider } from "@/components/ui";
import type { TaskBoardResponse, TaskSummary } from "@/lib/types";

import { TaskBoard } from "@/components/board/TaskBoard";

function task(id: string, title: string, epic: string | null): TaskSummary {
  return {
    id,
    number: Number(id.replace(/\D/g, "")) || 1,
    code: `SHOP-${id}`,
    title,
    priority: "medium",
    type: "Feature",
    status: "Draft",
    currentLane: "Drafting",
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
    createdAt: "2026-10-26T10:00:00Z",
    externalKey: epic ? `K-${id}` : null,
    externalUrl: null,
    externalEpic: epic,
  } as TaskSummary;
}

const board: TaskBoardResponse = {
  lanes: [
    {
      key: "Drafting",
      label: "Drafting",
      order: 0,
      tasks: [
        task("1", "Checkout card", "SHOP-1 Checkout"),
        task("2", "Search box", "SHOP-2 Search"),
        task("3", "Board-made chore", null),
      ],
    },
  ],
};

function show(data: TaskBoardResponse = board) {
  return renderWithPermissions(
    <ToastProvider>
      <TaskBoard boardData={data} projectId="proj-1" projectUlid="proj-1" />
    </ToastProvider>
  );
}

describe("Board epic filter", () => {
  it("lists the epics on the board plus tasks not from a tracker", () => {
    show();

    const filter = screen.getByTestId("board-epic-filter");
    const options = Array.from(filter.querySelectorAll("option")).map((o) => o.textContent);
    expect(options).toEqual([
      "boardPage.allEpics",
      "SHOP-1 Checkout",
      "SHOP-2 Search",
      "boardPage.notFromTracker",
    ]);
  });

  it("narrows the board to one epic", async () => {
    show();

    fireEvent.change(screen.getByTestId("board-epic-filter"), {
      target: { value: "SHOP-1 Checkout" },
    });

    expect(screen.getByText("Checkout card")).toBeInTheDocument();
    // Cards leave with an exit animation.
    await waitFor(() => expect(screen.queryByText("Search box")).toBeNull());
    await waitFor(() => expect(screen.queryByText("Board-made chore")).toBeNull());
  });

  it("can show only the tasks made on the board", async () => {
    show();

    const filter = screen.getByTestId("board-epic-filter") as HTMLSelectElement;
    const none = Array.from(filter.options).find((o) => o.textContent === "boardPage.notFromTracker")!;
    fireEvent.change(filter, { target: { value: none.value } });

    expect(screen.getByText("Board-made chore")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText("Checkout card")).toBeNull());
  });

  it("is absent when no task came from a tracker", () => {
    show({
      lanes: [{ key: "Drafting", label: "Drafting", order: 0, tasks: [task("3", "Board-made chore", null)] }],
    });

    expect(screen.queryByTestId("board-epic-filter")).toBeNull();
  });
});
