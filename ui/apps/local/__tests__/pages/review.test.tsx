import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen, fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage, renderPageWithPermissions } from "../render-page";


// The sent-back state offers "Review it myself now", which is the existing lane move.
const mockMoveTaskLane = vi.hoisted(() => vi.fn().mockResolvedValue(undefined));
vi.mock("@/lib/actions", () => ({
  moveTaskLane: mockMoveTaskLane,
}));

vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/tasks/[taskId]/review/PrReviewLive",
  () => ({
    PrReviewLive: (props: { task: { title: string }; projectId: string }) =>
      `PrReviewLive:${props.task.title}`,
  })
);

import PrReviewPage from "@/app/[locale]/(dashboard)/p/[projectId]/tasks/[taskId]/review/page";

// Canonical URLs use the per-project task number; legacy ULIDs redirect
const routeParams = { projectId: "proj-1", taskId: "1" };

describe("PrReviewPage", () => {
  beforeEach(() => {
    setRoute(routeParams);
  });

  it("shows task not found when task doesn't exist", async () => {
    mockApi.tasks.getByNumber.mockRejectedValue(new Error("Not found"));

    await renderPage(<PrReviewPage />);
    expect(screen.getByText("reviewPage.taskNotFound")).toBeInTheDocument();
  });

  it("shows no PR available when task not in CodeReview", async () => {
    mockApi.tasks.getByNumber.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "My Task",
      currentLane: "Developing",
      prUrl: null,
    });

    await renderPage(<PrReviewPage />);
    expect(screen.getByText("reviewPage.nothingTitle")).toBeInTheDocument();
  });

  it("shows no PR available when task has no prUrl", async () => {
    mockApi.tasks.getByNumber.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "My Task",
      currentLane: "CodeReview",
      prUrl: null,
    });

    await renderPage(<PrReviewPage />);
    expect(screen.getByText("reviewPage.nothingTitle")).toBeInTheDocument();
  });

  it("explains the sent-back state when the task has a PR but left CodeReview", async () => {
    // The reviewer asked for changes: the developer is on them and approve/merge reopen when
    // the task returns to Code Review. Non-technical users must not read this as "no PR".
    mockApi.tasks.getByNumber.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "My Task",
      currentLane: "Developing",
      prUrl: "https://github.com/orkka-repos/app/pull/9",
    });

    await renderPageWithPermissions(<PrReviewPage />);
    expect(screen.getByText("reviewPage.notOpenTitle")).toBeInTheDocument();
    expect(screen.queryByText("reviewPage.nothingTitle")).not.toBeInTheDocument();

    // The escape hatch: parks the task in Code Review as a human gate via the lane move.
    fireEvent.click(screen.getByRole("button", { name: "reviewMyself" }));
    await waitFor(() =>
      expect(mockMoveTaskLane).toHaveBeenCalledWith("task-1", "CodeReview")
    );
  });

  it("does not offer to take over the review while an agent is actively working", async () => {
    mockApi.tasks.getByNumber.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "My Task",
      currentLane: "Developing",
      status: "Active",
      prUrl: "https://github.com/orkka-repos/app/pull/9",
    });

    await renderPageWithPermissions(<PrReviewPage />);
    expect(screen.getByText("reviewPage.notOpenTitle")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "reviewMyself" })).toBeNull();
    expect(screen.getByText("activeHint")).toBeInTheDocument();
  });

  it("renders PrReviewLive when task is in CodeReview with prUrl", async () => {
    mockApi.tasks.getByNumber.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "Review This",
      currentLane: "CodeReview",
      prUrl: "https://github.com/org/repo/pull/1",
    });

    const { container } = await renderPage(<PrReviewPage />);
    expect(container.textContent).toContain("PrReviewLive:Review This");
  });

  it("renders breadcrumb for PR review", async () => {
    mockApi.tasks.getByNumber.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "Review This",
      currentLane: "CodeReview",
      prUrl: "https://github.com/org/repo/pull/1",
    });

    await renderPage(<PrReviewPage />);
    expect(screen.getByText("nav.board")).toBeInTheDocument();
    expect(screen.getByText("meta.reviewPr")).toBeInTheDocument();
  });
});
