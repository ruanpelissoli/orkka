import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";
import type { TaskMessage } from "@/lib/types";

// The banner reads a task shape this fixture does not bother to complete, and it is not
// what these tests are about.
vi.mock("@/components/task/InteractiveSessionBanner", () => ({
  InteractiveSessionBanner: () => null,
}));

// Stands in for the whole detail body so the assertions are about what the MODAL hands
// down and when, not about how the body draws it.
vi.mock("@/components/task/TaskDetailLive", () => ({
  TaskDetailLive: (props: {
    task: { title: string };
    messages: { id: string }[];
    messagesLoading?: boolean;
  }) => (
    <div>
      <span data-testid="title">{props.task.title}</span>
      <span data-testid="messages">{props.messages.map((m) => m.id).join(",")}</span>
      <span data-testid="messages-loading">{String(props.messagesLoading)}</span>
    </div>
  ),
}));

import { TaskDetailModal } from "@/components/board/TaskDetailModal";

/** A promise the test decides when to settle — a request still on the wire. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

function task(id: string, title: string) {
  return { id, number: 1, code: "ORK-1", title, currentLane: "Developing", status: "Active", priority: "High" };
}

function message(id: string): TaskMessage {
  return {
    id,
    taskId: "task-1",
    role: "assistant",
    content: id,
    messageType: "text",
    metadata: {},
    createdAt: "2026-09-01T10:00:00Z",
  };
}

describe("TaskDetailModal progressive load", () => {
  beforeEach(() => {
    // Everything except the task is left hanging: these are the seven reads that used to
    // share one Promise.all with it.
    mockApi.messages.list.mockReturnValue(new Promise(() => {}));
    mockApi.plans.latest.mockReturnValue(new Promise(() => {}));
    mockApi.attachments.list.mockReturnValue(new Promise(() => {}));
    mockApi.executions.list.mockReturnValue(new Promise(() => {}));
    mockApi.repos.list.mockReturnValue(new Promise(() => {}));
    mockApi.milestones.list.mockReturnValue(new Promise(() => {}));
    mockApi.agents.list.mockReturnValue(new Promise(() => {}));
  });

  it("paints the task without waiting for the other seven reads", async () => {
    mockApi.tasks.get.mockResolvedValue(task("task-1", "Fix login bug"));

    renderWithPermissions(
      <TaskDetailModal taskId="task-1" projectId="proj-1" onClose={() => {}} />
    );

    // The whole point: one slow endpoint used to hold the modal at its skeleton, and on a
    // lossy connection that made the slowest of eight requests the load time.
    expect(await screen.findByTestId("title")).toHaveTextContent("Fix login bug");
    // ...and the conversation says it is still coming rather than "no messages yet".
    expect(screen.getByTestId("messages-loading")).toHaveTextContent("true");
  });

  it("fills the conversation in when its own request lands", async () => {
    mockApi.tasks.get.mockResolvedValue(task("task-1", "Fix login bug"));
    const messages = deferred<TaskMessage[]>();
    mockApi.messages.list.mockReturnValue(messages.promise);

    renderWithPermissions(
      <TaskDetailModal taskId="task-1" projectId="proj-1" onClose={() => {}} />
    );
    await screen.findByTestId("title");

    messages.resolve([message("m1"), message("m2")]);

    await waitFor(() => expect(screen.getByTestId("messages")).toHaveTextContent("m1,m2"));
    expect(screen.getByTestId("messages-loading")).toHaveTextContent("false");
  });

  it("drops a late response belonging to a task the viewer has already flipped past", async () => {
    const first = deferred<TaskMessage[]>();
    mockApi.tasks.get.mockImplementation((id: string) =>
      Promise.resolve(task(id, id === "task-1" ? "First task" : "Second task"))
    );
    mockApi.messages.list.mockImplementation((id: string) =>
      id === "task-1" ? first.promise : Promise.resolve([message("m-second")])
    );

    const { rerender } = renderWithPermissions(
      <TaskDetailModal taskId="task-1" projectId="proj-1" onClose={() => {}} />
    );
    await screen.findByTestId("title");

    rerender(<TaskDetailModal taskId="task-2" projectId="proj-1" onClose={() => {}} />);
    await waitFor(() => expect(screen.getByTestId("title")).toHaveTextContent("Second task"));

    // The first task's conversation finally arrives — under the second task's header.
    first.resolve([message("m-first")]);

    await waitFor(() => expect(screen.getByTestId("messages")).toHaveTextContent("m-second"));
    expect(screen.getByTestId("messages")).not.toHaveTextContent("m-first");
  });

  it("shows the error state only when the task itself cannot be read", async () => {
    mockApi.tasks.get.mockRejectedValue(new Error("gone"));

    renderWithPermissions(
      <TaskDetailModal taskId="task-1" projectId="proj-1" onClose={() => {}} />
    );

    expect(await screen.findByText("taskDetailModal.retry")).toBeInTheDocument();
  });
});
