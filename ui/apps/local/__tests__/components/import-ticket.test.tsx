import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, fireEvent, waitFor, within, act } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";
import { ToastProvider } from "@/components/ui";
import type { TrackerImportPreview } from "@/lib/types";

import { ImportTicketButton } from "@/components/board/ImportTicketButton";

const preview: TrackerImportPreview = {
  kind: "jira",
  key: "SHOP-10",
  title: "Checkout flow",
  type: "Story",
  status: "In Progress",
  url: "https://acme.atlassian.net/browse/SHOP-10",
  level: "story",
  assignedToMe: false,
  description: "Pay by card.",
  attachments: [{ fileName: "flow.png", size: 10, mimeType: "image/png", cached: false }],
  subTasks: [
    { key: "SHOP-11", title: "Cart", status: "To Do", statusCategory: "todo", assignedToMe: false, selected: true, found: true, existingTaskId: null },
    { key: "SHOP-12", title: "Pay", status: "Done", statusCategory: "done", assignedToMe: true, selected: false, found: true, existingTaskId: null },
    { key: "SHOP-13", title: "Old", status: "To Do", statusCategory: "todo", assignedToMe: true, selected: false, found: true, existingTaskId: "task-13" },
  ],
  repoName: "shop",
  route: "single-repo",
  parentKey: null,
  existingTaskId: null,
  willBe: "story",
};

function show(projects = [{ slug: "shop", name: "Shop" }], onImported = vi.fn()) {
  renderWithPermissions(
    <ToastProvider>
      <ImportTicketButton projects={projects} onImported={onImported} />
    </ToastProvider>
  );
  fireEvent.click(screen.getByTestId("import-ticket-button"));
  return onImported;
}

async function readKey(key = "SHOP-10") {
  fireEvent.change(screen.getByTestId("import-ticket-key"), { target: { value: key } });
  fireEvent.click(screen.getByTestId("import-ticket-read"));
  await screen.findByTestId("import-ticket-preview");
}

describe("Import ticket", () => {
  beforeEach(() => {
    vi.useRealTimers();
    mockApi.trackers.previewImport.mockResolvedValue({ supported: true, ok: true, error: null, preview });
  });

  it("reads the ticket and ticks the open sub-tasks not on the board", async () => {
    show();
    await readKey("https://acme.atlassian.net/browse/SHOP-10");

    expect(mockApi.trackers.previewImport).toHaveBeenCalledWith("shop", "https://acme.atlassian.net/browse/SHOP-10", "");
    const rows = screen.getAllByTestId("import-ticket-subtask");
    const box = (i: number) => within(rows[i]).getByRole("checkbox") as HTMLInputElement;
    expect(box(0).checked).toBe(true);
    expect(box(1).checked).toBe(false);
    expect(box(2).disabled).toBe(true);
    expect(screen.getByTestId("import-ticket-will-be").textContent).toBe("willBeStory");
  });

  it("imports with the picked sub-tasks, follows the poll and opens the task", async () => {
    mockApi.trackers.requestImport.mockResolvedValue({
      id: "imp-1", ticketKey: "SHOP-10", subTaskKeys: ["SHOP-11", "SHOP-12"], state: "pending",
      taskId: null, error: null, requestedAt: "2026-11-20T10:00:00Z", completedAt: null,
    });
    mockApi.trackers.getImport.mockResolvedValue({
      id: "imp-1", ticketKey: "SHOP-10", subTaskKeys: ["SHOP-11", "SHOP-12"], state: "done",
      taskId: "task-1", error: null, requestedAt: "2026-11-20T10:00:00Z", completedAt: "2026-11-20T10:00:20Z",
    });
    const onImported = show();
    await readKey();
    fireEvent.click(within(screen.getAllByTestId("import-ticket-subtask")[1]).getByRole("checkbox"));

    vi.useFakeTimers({ shouldAdvanceTime: true });
    fireEvent.click(screen.getByTestId("import-ticket-import"));
    await screen.findByTestId("import-ticket-queued");
    expect(mockApi.trackers.requestImport).toHaveBeenCalledWith("shop", "SHOP-10", ["SHOP-11", "SHOP-12"], "");

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3500);
    });
    const done = await screen.findByTestId("import-ticket-done");
    fireEvent.click(within(done).getByRole("button"));
    expect(onImported).toHaveBeenCalledWith("task-1", "shop");
  });

  it("says why a ticket cannot be read", async () => {
    mockApi.trackers.previewImport.mockResolvedValue({
      supported: true, ok: false, error: "SHOP-1 is above a story (an epic or a feature).", preview: null,
    });
    show();
    fireEvent.change(screen.getByTestId("import-ticket-key"), { target: { value: "SHOP-1" } });
    fireEvent.click(screen.getByTestId("import-ticket-read"));

    expect((await screen.findByTestId("import-ticket-error")).textContent).toContain("above a story");
  });

  it("asks for a project when the board shows several", async () => {
    show([{ slug: "shop", name: "Shop" }, { slug: "billing", name: "Billing" }]);
    fireEvent.change(screen.getByTestId("import-ticket-key"), { target: { value: "SHOP-10" } });
    expect((screen.getByTestId("import-ticket-read") as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(screen.getByTestId("import-ticket-project"), { target: { value: "billing" } });
    fireEvent.click(screen.getByTestId("import-ticket-read"));
    await waitFor(() => expect(mockApi.trackers.previewImport).toHaveBeenCalledWith("billing", "SHOP-10", ""));
  });
});
