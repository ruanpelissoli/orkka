import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { ownerMe } from "../render-with-permissions";
import type { PullRequestDetail } from "@/lib/types";

vi.mock("@/lib/actions", () => ({
  submitPrReview: vi.fn().mockResolvedValue(undefined),
}));

import { PrReviewActions } from "@/components/pr/PrReviewActions";

/**
 * Manual review controls exist only when the pipeline is waiting on a human.
 *
 * They used to render for every open PR — including while the reviewer agent was mid-run. That
 * read as an instruction: a user approved by hand because the UI told them to, the merge landed,
 * and the agent's verdict arrived seconds later against a finished task. The status gate removes
 * the invitation to that race.
 */
const openPr = {
  state: "open",
  files: [],
  reviews: [],
  comments: [],
} as unknown as PullRequestDetail;

function show(taskStatus?: string) {
  return render(
    <PermissionsProvider me={ownerMe}>
      <PrReviewActions
        pr={openPr}
        taskId="task-1"
        projectId="proj-1"
        taskStatus={taskStatus as never}
      />
    </PermissionsProvider>
  );
}

describe("PrReviewActions status gate", () => {
  it("offers no approve button while the reviewer agent is still working", () => {
    show("Active");

    // The buttons are the invitation into the race — a note replaces them until the task
    // genuinely waits on a human.
    expect(screen.queryByText("approve")).toBeNull();
    expect(screen.getByText("agentStillReviewing")).toBeTruthy();
  });

  it("offers the controls once the task waits on a human", () => {
    show("WaitingCodeReview");

    expect(screen.queryByText("agentStillReviewing")).toBeNull();
    // Whatever the button labels are keyed as, the informational note must be gone and
    // SOMETHING actionable rendered.
    expect(document.querySelector("button")).not.toBeNull();
  });

  it("keeps legacy callers working when no status is passed", () => {
    show(undefined);

    // Callers that predate the gate (none in-tree, but the prop is optional) keep the old
    // behavior rather than silently losing their controls.
    expect(screen.queryByText("agentStillReviewing")).toBeNull();
    expect(document.querySelector("button")).not.toBeNull();
  });
});
