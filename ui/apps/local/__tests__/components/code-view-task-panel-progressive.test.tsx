import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import type { TaskMessage } from "@/lib/types";

vi.mock("@/components/code-view/PinnedTaskHeader", () => ({
  PinnedTaskHeader: (props: { task: { title: string } }) => <h1>{props.task.title}</h1>,
}));
vi.mock("@/components/task/InteractiveSessionBanner", () => ({
  InteractiveSessionBanner: () => null,
}));
vi.mock("@/components/task/InteractiveApprovalPanel", () => ({
  InteractiveApprovalPanel: (props: { initialApprovals: { id: string }[] }) => (
    <span data-testid="approvals">{props.initialApprovals.map((a) => a.id).join(",")}</span>
  ),
}));
vi.mock("@/components/task/TaskDetailLive", () => ({
  TaskDetailLive: (props: { messages: { id: string }[]; messagesLoading?: boolean }) => (
    <div>
      <span data-testid="messages">{props.messages.map((m) => m.id).join(",")}</span>
      <span data-testid="messages-loading">{String(props.messagesLoading)}</span>
    </div>
  ),
}));

import { CodeViewTaskPanel } from "@/components/code-view/CodeViewTaskPanel";

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
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

function panel(taskId = "task-1") {
  return (
    <CodeViewTaskPanel
      taskId={taskId}
      projectId="proj-1"
      totals={{ fileCount: 0, additions: 0, deletions: 0, hasLive: false }}
      agentActive={false}
      agentName={null}
      onTaskLoaded={() => {}}
      onLiveTaskChange={() => {}}
      onStreamEvent={() => {}}
      onWorkspaceChanged={() => {}}
      onAgentActiveChange={() => {}}
      onDeselect={() => {}}
    />
  );
}

describe("CodeViewTaskPanel progressive load", () => {
  beforeEach(() => {
    mockApi.tasks.get.mockResolvedValue({
      id: "task-1", number: 1, code: "ORK-1", title: "Fix login bug",
      currentLane: "Developing", status: "Active", priority: "High",
    });
    // The five reads that used to share one Promise.all with the task.
    mockApi.messages.list.mockReturnValue(new Promise(() => {}));
    mockApi.plans.latest.mockReturnValue(new Promise(() => {}));
    mockApi.attachments.list.mockReturnValue(new Promise(() => {}));
    mockApi.executions.list.mockReturnValue(new Promise(() => {}));
    mockApi.tasks.listApprovals.mockReturnValue(new Promise(() => {}));
  });

  it("paints the task without waiting for the other five reads", async () => {
    render(panel());

    expect(await screen.findByText("Fix login bug")).toBeInTheDocument();
    expect(screen.getByTestId("messages-loading")).toHaveTextContent("true");
  });

  it("fills each part in as its own request lands", async () => {
    const messages = deferred<TaskMessage[]>();
    const approvals = deferred<{ id: string }[]>();
    mockApi.messages.list.mockReturnValue(messages.promise);
    mockApi.tasks.listApprovals.mockReturnValue(approvals.promise);

    render(panel());
    await screen.findByText("Fix login bug");

    messages.resolve([message("m1")]);
    await waitFor(() => expect(screen.getByTestId("messages")).toHaveTextContent("m1"));
    expect(screen.getByTestId("messages-loading")).toHaveTextContent("false");
    // Independent: the conversation landing says nothing about the approvals.
    expect(screen.getByTestId("approvals")).toHaveTextContent("");

    approvals.resolve([{ id: "a1" }]);
    await waitFor(() => expect(screen.getByTestId("approvals")).toHaveTextContent("a1"));
  });

  it("drops a late response belonging to a task the viewer has already flipped past", async () => {
    const first = deferred<TaskMessage[]>();
    mockApi.tasks.get.mockImplementation((id: string) =>
      Promise.resolve({
        id, number: 1, code: "ORK-1",
        title: id === "task-1" ? "First task" : "Second task",
        currentLane: "Developing", status: "Active", priority: "High",
      })
    );
    mockApi.messages.list.mockImplementation((id: string) =>
      id === "task-1" ? first.promise : Promise.resolve([message("m-second")])
    );

    const { rerender } = render(panel("task-1"));
    await screen.findByText("First task");

    rerender(panel("task-2"));
    await screen.findByText("Second task");
    await waitFor(() => expect(screen.getByTestId("messages")).toHaveTextContent("m-second"));

    first.resolve([message("m-first")]);

    await waitFor(() => expect(screen.getByTestId("messages")).not.toHaveTextContent("m-first"));
  });
});
