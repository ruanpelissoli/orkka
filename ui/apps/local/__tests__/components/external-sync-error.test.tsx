import { describe, it, expect } from "vitest";
import { render, screen, within } from "@testing-library/react";
import { DndContext } from "@dnd-kit/core";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";
import { ToastProvider } from "@/components/ui";
import type { Task, TaskSummary } from "@/lib/types";

import { TaskCard } from "@/components/board/TaskCard";
import { TaskDetailEditable } from "@/components/task/TaskDetailEditable";

const SYNC_ERROR = "Couldn't move SHOP-12 to “In Review” in Jira";

const ticket = {
  externalKey: "SHOP-12",
  externalUrl: "https://acme.atlassian.net/browse/SHOP-12",
  externalEpic: "SHOP-1 Checkout",
};

function summary(overrides: Partial<TaskSummary> = {}): TaskSummary {
  return {
    id: "task-1",
    number: 7,
    code: "SHOP-7",
    title: "[SHOP-12] Add invoice export",
    priority: "medium",
    type: "Feature",
    status: "Active",
    currentLane: "Developing",
    branchName: null,
    prUrl: null,
    queuePosition: null,
    tags: [],
    repoId: "repo-1",
    repoName: "api",
    repoColor: null,
    assigneeId: null,
    assigneeName: null,
    assigneeAvatarUrl: null,
    isBlocked: false,
    blockedBy: [],
    mergeFailureReason: null,
    createdAt: "2026-10-26T10:00:00Z",
    ...ticket,
    externalSyncError: SYNC_ERROR,
    ...overrides,
  } as TaskSummary;
}

function task(overrides: Partial<Task> = {}): Task {
  return {
    id: "task-1",
    orgId: "org-1",
    projectId: "proj-1",
    number: 7,
    code: "SHOP-7",
    repoId: "repo-1",
    title: "[SHOP-12] Add invoice export",
    description: "",
    priority: "medium",
    type: "Feature",
    status: "Active",
    currentLane: "Developing",
    branchName: null,
    prUrl: null,
    isMerged: false,
    mergeFailureReason: null,
    queuePosition: null,
    tags: [],
    contextFiles: [],
    contextDocumentIds: [],
    isArchived: false,
    isBlocked: false,
    dependsOn: [],
    dependents: [],
    assigneeId: null,
    createdAt: "2026-10-26T10:00:00Z",
    startedAt: null,
    completedAt: null,
    repoName: "api",
    repoCardColor: null,
    ...ticket,
    externalSyncError: SYNC_ERROR,
    ...overrides,
  } as Task;
}

describe("Tracker status sync failure", () => {
  it("puts a compact warning with the full text as tooltip on the board card", () => {
    render(
      <DndContext>
        <TaskCard task={summary()} />
      </DndContext>
    );

    const warning = screen.getByTestId("external-sync-error");
    expect(warning).toHaveAttribute("title", SYNC_ERROR);
    expect(warning).toHaveAttribute("aria-label", SYNC_ERROR);
  });

  it("shows no warning once a later move succeeded", () => {
    render(
      <DndContext>
        <TaskCard task={summary({ externalSyncError: null })} />
      </DndContext>
    );

    expect(screen.queryByTestId("external-sync-error")).toBeNull();
  });

  it("explains it on the task page and links to the ticket source settings", () => {
    renderWithPermissions(
      <ToastProvider>
        <TaskDetailEditable task={task()} projectId="shop" />
      </ToastProvider>
    );

    const line = screen.getByTestId("external-sync-error");
    expect(within(line).getByText(SYNC_ERROR)).toBeInTheDocument();
    expect(line).toHaveTextContent("externalTicket.syncHint");
    expect(within(line).getByRole("link", { name: "externalTicket.syncHintLink" })).toHaveAttribute(
      "href",
      "/p/shop/settings#ticket-source"
    );
    // The ticket link itself stays next to it.
    expect(screen.getByTestId("external-ticket-badge")).toHaveAttribute(
      "href",
      "https://acme.atlassian.net/browse/SHOP-12"
    );
  });

  it("is absent on the task page when the ticket is in sync", () => {
    renderWithPermissions(
      <ToastProvider>
        <TaskDetailEditable task={task({ externalSyncError: null })} projectId="shop" />
      </ToastProvider>
    );

    expect(screen.queryByTestId("external-sync-error")).toBeNull();
    expect(screen.getByTestId("external-epic")).toHaveTextContent("externalTicket.epic");
  });
});
