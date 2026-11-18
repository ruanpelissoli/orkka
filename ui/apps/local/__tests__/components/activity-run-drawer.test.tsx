import { describe, it, expect, beforeEach, vi } from "vitest";
import { fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";

const retryTask = vi.fn();
vi.mock("@/lib/actions", () => ({ retryTask: (...args: unknown[]) => retryTask(...args) }));

import { renderWithPermissions, memberMe } from "../render-with-permissions";
import { mockApi } from "../mocks/api";
import { RunDrawer } from "@/components/activity";
import { ToastProvider } from "@/components/ui";
import type { TaskExecution } from "@/lib/types";

function execution(overrides: Partial<TaskExecution> = {}): TaskExecution {
  return {
    id: "e1",
    taskId: "t1",
    agentId: "a1",
    agentName: "Pixel",
    agentRole: "FrontendDev",
    lane: "Developing",
    status: "Failed",
    agentLog: "resolving imports\nnpm test\n2 failing",
    filesChanged: [
      { path: "src/app/page.tsx", action: "modified" },
      { path: "src/app/new.tsx", action: "added" },
    ],
    tokensUsed: 4_000,
    promptTokens: 3_000,
    completionTokens: 1_000,
    cost: 0.42,
    billed: {
      inputTokens: 3_000,
      outputTokens: 1_000,
      cacheReadTokens: 900_000,
      cacheWriteTokens: 20_000,
      totalTokens: 924_000,
    },
    errorMessage: "2 tests failing in auth.spec.ts",
    startedAt: "2026-08-17T01:00:00Z",
    completedAt: "2026-08-17T01:04:00Z",
    createdAt: "2026-08-17T00:59:00Z",
    ...overrides,
  };
}

function renderDrawer(
  target: { executionId: string; taskId: string } | null = { executionId: "e1", taskId: "t1" },
  me = undefined as ReturnType<typeof memberMe> | undefined
) {
  const onClose = vi.fn();
  const result = renderWithPermissions(
    <ToastProvider>
      <RunDrawer target={target} onClose={onClose} />
    </ToastProvider>,
    me
  );
  return { ...result, onClose };
}

describe("RunDrawer", () => {
  beforeEach(() => {
    retryTask.mockReset();
    mockApi.executions.list.mockReset();
    mockApi.executions.list.mockResolvedValue([execution()]);
  });

  it("stays shut with no run selected, and never fetches", () => {
    const { container } = renderDrawer(null);
    expect(container.querySelector('[data-testid="run-drawer"]')).toBeNull();
    expect(mockApi.executions.list).not.toHaveBeenCalled();
  });

  it("shows the picked run's log, files and error", async () => {
    const { findByTestId } = renderDrawer();
    const drawer = await findByTestId("run-drawer");

    expect(mockApi.executions.list).toHaveBeenCalledWith("t1", expect.anything());
    expect(drawer.textContent).toContain("2 tests failing in auth.spec.ts");
    expect(drawer.textContent).toContain("2 failing");
    expect(drawer.textContent).toContain("src/app/page.tsx");
  });

  it("picks the clicked run out of the task's runs", async () => {
    mockApi.executions.list.mockResolvedValue([
      execution({ id: "e1", errorMessage: "the old one" }),
      execution({ id: "e2", errorMessage: "the clicked one" }),
    ]);

    const { findByTestId } = renderDrawer({ executionId: "e2", taskId: "t1" });
    const drawer = await findByTestId("run-drawer");

    expect(drawer.textContent).toContain("the clicked one");
    expect(drawer.textContent).not.toContain("the old one");
  });

  it("reports billed tokens, not the execution row's undercount", async () => {
    const { findByTestId } = renderDrawer();
    const drawer = await findByTestId("run-drawer");

    // 924k billed, including cache reads — `tokensUsed` alone would claim 4k.
    expect(drawer.textContent).toContain("924");
    expect(drawer.textContent).not.toContain("4,000");
  });

  it("prices the run as a list-price estimate in dollars, not credits", async () => {
    const { findByTestId } = renderDrawer();
    const drawer = await findByTestId("run-drawer");

    expect(drawer.textContent).toContain("≈ $0.42");
    expect(drawer.textContent).not.toContain("420");
  });

  it("says so plainly when the run has aged out of the window", async () => {
    mockApi.executions.list.mockResolvedValue([]);
    const { findByTestId } = renderDrawer();
    const drawer = await findByTestId("run-drawer");
    expect(drawer.textContent).toContain("runGone");
  });

  it("re-runs the task and closes", async () => {
    retryTask.mockResolvedValue(undefined);
    const { findByText, onClose } = renderDrawer();

    fireEvent.click(await findByText("retryTask"));

    await waitFor(() => expect(retryTask).toHaveBeenCalledWith("t1"));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it("hides the retry button from a member without TasksExecute", async () => {
    const { findByTestId } = renderDrawer({ executionId: "e1", taskId: "t1" }, memberMe([]));
    const drawer = await findByTestId("run-drawer");
    expect(drawer.textContent).not.toContain("retryTask");
  });
});
