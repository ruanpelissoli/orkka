import { describe, it, expect, beforeEach } from "vitest";
import { render } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { ownerMe } from "../render-with-permissions";
import type { MilestoneDetail } from "@/lib/types";

import { MilestoneDetailClient } from "@/components/roadmap/MilestoneDetailClient";

/**
 * Every milestone ships as it completes — each task lands on the default branch as it merges —
 * so the milestone page shows its status and nothing about a separate ship step.
 */
function detail(status: MilestoneDetail["milestone"]["status"]): MilestoneDetail {
  return {
    milestone: {
      id: "ms-1",
      projectId: "proj-1",
      title: "Checkout flow",
      outcome: "Users can pay.",
      status,
      position: 0,
      startedAt: null,
      completedAt: null,
      counts: { total: 2, done: 2, inFlight: 0, needsAttention: 0, notStarted: 0 },
      createdAt: new Date().toISOString(),
      repoId: null,
      repoName: null,
      shippedAt: null,
    },
    tasks: [],
    previousUnfinished: null,
  };
}

function show(initial: MilestoneDetail) {
  return render(
    <PermissionsProvider me={ownerMe}>
      <MilestoneDetailClient projectId="proj-1" initial={initial} />
    </PermissionsProvider>
  );
}

describe("MilestoneDetailClient status", () => {
  beforeEach(() => {
    mockApi.milestones.get.mockReset();
    mockApi.milestones.get.mockResolvedValue(detail("Completed"));
  });

  it("shows a completed milestone as completed", () => {
    const { container } = show(detail("Completed"));

    expect(container.textContent).toContain("roadmapPage.status.Completed");
  });

  it("never renders ship-state, integration-branch or retry UI", () => {
    const { container } = show(detail("Completed"));

    expect(container.textContent).not.toContain("milestonePage.ship");
  });
});
