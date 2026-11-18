/**
 * "Answer every question" is chosen on the autopilot start card, for that session only.
 * Pinned here: it is offered only to members who could set it back when it was a project
 * setting (ProjectsManage — the endpoint enforces the same bar), it always starts unticked,
 * the start request carries the choice, and a running session that answers everything says so.
 */
import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { memberMe, renderWithPermissions } from "../render-with-permissions";

vi.mock("@/hooks/use-client-api", () => ({
  useClientApi:
    () =>
    <T,>(fn: (token: string) => Promise<T>) =>
      fn("test-token"),
}));

import { AutopilotToggle } from "@/components/board/AutopilotToggle";
import type { AutopilotStatus } from "@/lib/types";

const idle: AutopilotStatus = {
  running: false,
  sessionId: null,
  startedAt: null,
  stoppedAt: null,
  stopReason: null,
  capUsd: null,
  spentUsd: 0,
  tasksSubmitted: 0,
  tasksCompleted: 0,
  tasksFailed: 0,
  tasksInFlight: 0,
  tasksWaitingOnYou: 0,
  draftingRemaining: 3,
};

function openCard(status: AutopilotStatus, me = memberMe(["TasksExecute", "ProjectsManage"])) {
  renderWithPermissions(
    <AutopilotToggle projectId="proj-1" projectUlid="01PROJ" initialStatus={status} />,
    me
  );
  fireEvent.click(screen.getByRole("button", { name: "autopilotToggle.name" }));
}

describe("AutopilotToggle — answer every question", () => {
  beforeEach(() => {
    mockApi.autopilot.start.mockClear();
    mockApi.autopilot.start.mockResolvedValue({ ...idle, running: true, sessionId: "s1" });
  });

  it("offers the choice unticked, and sends it when ticked", async () => {
    openCard(idle);

    const box = screen.getByTestId("autopilot-answer-every-question");
    expect(box).not.toBeChecked();

    fireEvent.click(box);
    fireEvent.click(screen.getByRole("button", { name: "autopilotToggle.start" }));

    await waitFor(() => expect(mockApi.autopilot.start).toHaveBeenCalledTimes(1));
    const [projectId, payload] = mockApi.autopilot.start.mock.calls[0];
    expect(projectId).toBe("proj-1");
    expect(payload).toMatchObject({ capUsd: 20, answerEveryQuestion: true });
  });

  it("sends false when left unticked", async () => {
    openCard(idle);

    fireEvent.click(screen.getByRole("button", { name: "autopilotToggle.start" }));

    await waitFor(() => expect(mockApi.autopilot.start).toHaveBeenCalledTimes(1));
    expect(mockApi.autopilot.start.mock.calls[0][1]).toMatchObject({ answerEveryQuestion: false });
  });

  it("is not offered without ProjectsManage", () => {
    // Starting a session only needs TasksExecute. This choice lets agents decide deletions,
    // billing and production questions, so it keeps the bar it had as a project setting.
    openCard(idle, memberMe(["TasksExecute"]));

    expect(screen.queryByTestId("autopilot-answer-every-question")).not.toBeInTheDocument();
  });

  it("never pre-ticks from the last session", () => {
    // A remembered "answer everything" is how it outlives the night it was meant for.
    openCard({ ...idle, stopReason: "Drained", answerEveryQuestion: true });

    expect(screen.getByTestId("autopilot-answer-every-question")).not.toBeChecked();
  });

  it("says so while a session that answers everything is running", () => {
    openCard({ ...idle, running: true, sessionId: "s1", answerEveryQuestion: true });

    expect(screen.getByTestId("autopilot-answering-every-question")).toBeInTheDocument();
  });

  it("stays quiet while a session with the guardrails on is running", () => {
    openCard({ ...idle, running: true, sessionId: "s1" });

    expect(screen.queryByTestId("autopilot-answering-every-question")).not.toBeInTheDocument();
  });
});
