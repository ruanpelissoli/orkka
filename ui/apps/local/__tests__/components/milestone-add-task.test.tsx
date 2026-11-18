import { describe, it, expect, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { ownerMe, memberMe } from "../render-with-permissions";
import type { MilestoneDetail, OrgMe } from "@/lib/types";

import { MilestoneDetailClient } from "@/components/roadmap/MilestoneDetailClient";

/**
 * The planner fills a milestone, but it can miss something the user knows about. The milestone
 * page therefore also offers the ordinary create form, preselected on this milestone and
 * returning here afterwards — gated like the planner button, since both create tasks.
 */
function detail(tasks: MilestoneDetail["tasks"] = []): MilestoneDetail {
  return {
    milestone: {
      id: "ms-1",
      projectId: "proj-1",
      title: "Checkout flow",
      outcome: "Users can pay.",
      status: "Planned",
      position: 0,
      startedAt: null,
      completedAt: null,
      counts: { total: tasks.length, done: 0, inFlight: 0, needsAttention: 0, notStarted: tasks.length },
      createdAt: new Date().toISOString(),
      repoId: null,
      repoName: null,
      shippedAt: null,
    },
    tasks,
    previousUnfinished: null,
  };
}

function show(initial: MilestoneDetail, me: OrgMe = ownerMe) {
  return render(
    <PermissionsProvider me={me}>
      <MilestoneDetailClient projectId="proj-1" initial={initial} />
    </PermissionsProvider>
  );
}

describe("MilestoneDetailClient add task", () => {
  beforeEach(() => {
    mockApi.milestones.get.mockReset();
    mockApi.milestones.get.mockResolvedValue(detail());
  });

  it("links to the create form preselected on this milestone, returning here after", () => {
    show(detail());

    const link = screen.getByText("milestonePage.addTask").closest("a");
    expect(link).toHaveAttribute(
      "href",
      "/p/proj-1/tasks/new?from=milestone&milestoneId=ms-1"
    );
  });

  it("is offered alongside the planner, even once the milestone already has tasks", () => {
    show(
      detail([
        { id: "t1", number: 7, title: "Cart page", type: "Feature", priority: "medium", lane: "Drafting", status: "Draft" },
      ])
    );

    expect(screen.getByText("milestonePage.addTask")).toBeInTheDocument();
    expect(screen.getByText("milestonePage.planMoreTasks")).toBeInTheDocument();
  });

  it("is hidden from members who cannot create tasks", () => {
    show(detail(), memberMe([]));

    expect(screen.queryByText("milestonePage.addTask")).not.toBeInTheDocument();
    expect(screen.queryByText("milestonePage.generateTasks")).not.toBeInTheDocument();
  });

  it("is shown to members granted TasksCreate", () => {
    show(detail(), memberMe(["TasksCreate"]));

    expect(screen.getByText("milestonePage.addTask")).toBeInTheDocument();
  });
});
