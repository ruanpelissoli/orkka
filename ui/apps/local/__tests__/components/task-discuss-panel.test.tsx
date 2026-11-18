import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "../mocks/navigation";
import "../mocks/i18n-navigation";
import { mockApi, resetApiMocks } from "../mocks/api";
import { renderWithPermissions, memberMe } from "../render-with-permissions";
import { ToastProvider } from "@/components/ui";
import { TaskDiscussPanel } from "@/components/task/TaskDiscussPanel";
import { taskDiscussApplyMode, TASK_DISCUSS_APPLY_KEYS } from "@/components/task/task-discuss-labels";
import type { TaskChat } from "@/lib/types";

// Like the shared intl mock (keys echo back), but interpolated values are appended so
// the toast's lane is assertable.
vi.mock("use-intl", () => {
  const t = (key: string, values?: Record<string, unknown>) =>
    values ? `${key} ${JSON.stringify(values)}` : key;
  t.rich = (key: string) => key;
  t.markup = (key: string) => key;
  t.raw = (key: string) => key;
  t.has = () => true;
  return {
    useTranslations: () => t,
    useLocale: () => "en",
    useFormatter: () => ({ dateTime: (d: Date) => d.toISOString(), number: String }),
  };
});

function chat(overrides: Partial<TaskChat> = {}): TaskChat {
  return {
    conversationId: "conv-1",
    taskId: "task-1",
    agentId: "agent-1",
    agentName: "Ada",
    agentRole: "FullStackDev",
    defaultAgentId: "agent-1",
    lane: "PendingQuestions",
    hasActiveRun: false,
    messages: [
      { id: "m-user", role: "user", content: "Which table holds orders?", createdAt: "2026-11-17T10:00:00Z" },
      { id: "m-reply", role: "assistant", content: "The `orders` table.", createdAt: "2026-11-17T10:00:05Z" },
    ],
    agents: [
      { id: "agent-1", name: "Ada", role: "FullStackDev" },
      { id: "agent-2", name: "Luna", role: "PM" },
    ],
    ...overrides,
  };
}

function renderPanel(lane: string, onApplied = vi.fn(), me = undefined as Parameters<typeof renderWithPermissions>[1]) {
  return renderWithPermissions(
    <ToastProvider>
      <TaskDiscussPanel taskId="task-1" projectId="proj-1" lane={lane} onApplied={onApplied} />
    </ToastProvider>,
    me
  );
}

describe("task discuss apply labels", () => {
  it("answers questions in Pending Questions", () => {
    expect(taskDiscussApplyMode("PendingQuestions")).toBe("answer");
    expect(TASK_DISCUSS_APPLY_KEYS.answer).toEqual({ message: "applyAnswer", summary: "summaryAnswer" });
  });

  it("requests plan changes in Plan Ready", () => {
    expect(taskDiscussApplyMode("PlanReady")).toBe("planChanges");
  });

  it("posts to the task in any other lane", () => {
    for (const lane of ["Drafting", "Ready", "Planning", "Developing", "CodeReview", "Done", undefined]) {
      expect(taskDiscussApplyMode(lane)).toBe("post");
    }
  });
});

describe("TaskDiscussPanel", () => {
  beforeEach(() => {
    resetApiMocks();
    mockApi.tasks.getChat.mockResolvedValue(chat());
  });

  it("loads the default agent's conversation", async () => {
    renderPanel("PendingQuestions");
    expect(await screen.findByText("Which table holds orders?")).toBeInTheDocument();
    expect(mockApi.tasks.getChat).toHaveBeenCalledWith("task-1", null, "");
    expect(screen.getByRole("combobox")).toHaveValue("agent-1");
  });

  it.each([
    ["PendingQuestions", "applyAnswer", "summaryAnswer"],
    ["PlanReady", "applyPlanChanges", "summaryPlanChanges"],
    ["Developing", "applyPost", "summaryPost"],
  ])("labels the apply actions for %s", async (lane, message, summary) => {
    renderPanel(lane);
    expect(await screen.findByRole("button", { name: message })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: summary })).toBeInTheDocument();
  });

  it("applies a reply, toasts the new lane and tells the host", async () => {
    mockApi.tasks.applyChat.mockResolvedValue({
      lane: "Planning",
      status: "InQueue",
      action: "answered",
      messageId: "tm-1",
      content: "From discussion: The `orders` table.",
    });
    const onApplied = vi.fn();
    renderPanel("PendingQuestions", onApplied);

    await userEvent.click(await screen.findByRole("button", { name: "applyAnswer" }));

    await waitFor(() =>
      expect(mockApi.tasks.applyChat).toHaveBeenCalledWith(
        "task-1",
        { messageId: "m-reply", agentId: "agent-1" },
        ""
      )
    );
    const toast = await screen.findByRole("status");
    expect(toast).toHaveTextContent("appliedAnswered");
    expect(toast).toHaveTextContent("lanes.Planning");
    expect(onApplied).toHaveBeenCalledWith(expect.objectContaining({ lane: "Planning" }));
  });

  it("applies a summary of the discussion with the picked agent", async () => {
    mockApi.tasks.applyChat.mockResolvedValue({
      lane: "PlanReady",
      status: "Active",
      action: "posted",
      messageId: "tm-2",
      content: "From discussion: …",
    });
    renderPanel("Developing");

    await userEvent.click(await screen.findByRole("button", { name: "summaryPost" }));

    await waitFor(() =>
      expect(mockApi.tasks.applyChat).toHaveBeenCalledWith("task-1", { summarize: true, agentId: "agent-1" }, "")
    );
    expect(await screen.findByRole("status")).toHaveTextContent("appliedPosted");
  });

  it("switching agents loads that agent's conversation", async () => {
    renderPanel("Developing");
    await screen.findByText("Which table holds orders?");
    mockApi.tasks.getChat.mockResolvedValue(
      chat({ agentId: "agent-2", agentName: "Luna", messages: [] })
    );

    await userEvent.selectOptions(screen.getByRole("combobox"), "agent-2");

    await waitFor(() => expect(mockApi.tasks.getChat).toHaveBeenLastCalledWith("task-1", "agent-2", ""));
    expect(await screen.findByText(/intro/)).toBeInTheDocument();
  });

  it("hides send and apply without TasksExecute", async () => {
    renderPanel("PendingQuestions", vi.fn(), memberMe([]));
    await screen.findByText("Which table holds orders?");
    expect(screen.queryByRole("button", { name: "applyAnswer" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "send" })).not.toBeInTheDocument();
  });
});
