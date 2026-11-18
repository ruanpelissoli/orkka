import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";

import { ExecutionHistory } from "@/components/task/ExecutionHistory";
import type { TaskExecution } from "@/lib/types";

/**
 * Shaped from a real 17-minute Opus run: 801 input, 70,345 output, 5.89M cache reads,
 * 100,481 cache writes, $5.87. Showing prompt+completion (71,146) beside that cost displays
 * 1.2% of the billed volume and reads as a 6x overcharge — which is the bug this covers.
 */
function makeExecution(overrides: Partial<TaskExecution> = {}): TaskExecution {
  return {
    id: "e1",
    taskId: "t1",
    agentId: null,
    agentName: null,
    agentRole: null,
    lane: "Developing",
    status: "Completed",
    agentLog: null,
    filesChanged: [],
    tokensUsed: 71_146,
    promptTokens: 801,
    completionTokens: 70_345,
    cost: 5.87,
    billed: {
      inputTokens: 801,
      outputTokens: 70_345,
      cacheReadTokens: 5_891_523,
      cacheWriteTokens: 100_481,
      totalTokens: 6_063_150,
    },
    errorMessage: null,
    startedAt: "2026-08-10T10:00:00Z",
    completedAt: "2026-08-10T10:17:07Z",
    createdAt: "2026-08-10T10:00:00Z",
    ...overrides,
  } as TaskExecution;
}

describe("ExecutionHistory", () => {
  it("headlines the billed token total, not prompt+completion", () => {
    render(<ExecutionHistory executions={[makeExecution()]} />);

    // 6.06M billed, not the 71k the execution row stores.
    expect(screen.getByText(/6\.1M/)).toBeInTheDocument();
    expect(screen.queryByText(/71k/)).not.toBeInTheDocument();
  });

  it("breaks the cost down to the cache buckets when expanded", async () => {
    const { default: userEvent } = await import("@testing-library/user-event");
    render(<ExecutionHistory executions={[makeExecution()]} />);

    await userEvent.click(screen.getByRole("button"));

    expect(screen.getByText("billedTokens")).toBeInTheDocument();
    expect(screen.getByText("cacheRead")).toBeInTheDocument();
    expect(screen.getByText("5,891,523")).toBeInTheDocument();
    expect(screen.getByText("cacheWrite")).toBeInTheDocument();
    expect(screen.getByText("100,481")).toBeInTheDocument();

    // The share is the point: it's what explains the bill at a glance.
    expect(screen.getByText("(97%)")).toBeInTheDocument();
  });

  it("falls back to prompt/completion when the run has no usage rows yet", async () => {
    const { default: userEvent } = await import("@testing-library/user-event");
    render(
      <ExecutionHistory
        executions={[makeExecution({ billed: null, cost: 0, status: "Running" })]}
      />
    );

    expect(screen.getByText(/71k/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button"));

    expect(screen.getByText(/promptLabel 801/)).toBeInTheDocument();
    expect(screen.getByText(/completionLabel 70,345/)).toBeInTheDocument();
    expect(screen.queryByText("billedTokens")).not.toBeInTheDocument();
  });
});
