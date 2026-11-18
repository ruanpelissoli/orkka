import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { mockI18nRouter } from "../mocks/i18n-navigation";
import { setRoute } from "../mocks/navigation";
import { renderPage } from "../render-page";

vi.mock("@/components/task/TaskDetailLive", () => ({
  TaskDetailLive: (props: { task: { title: string }; projectId: string }) =>
    `TaskDetailLive:${props.task.title}`,
}));

import TaskDetailPage from "@/app/[locale]/(dashboard)/p/[projectId]/tasks/[taskId]/page";

// Canonical URLs use the per-project task number; the page replaces legacy
// ULID URLs with /p/{project}/tasks/{number}
const routeParams = { projectId: "proj-1", taskId: "1" };

describe("TaskDetailPage", () => {
  beforeEach(() => {
    setRoute(routeParams);
    mockApi.tasks.getByNumber.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "Fix login bug",
      currentLane: "Developing",
      priority: "High",
    });
    mockApi.tasks.get.mockResolvedValue({
      id: "task-1",
      number: 1,
      code: "ORK-1",
      title: "Fix login bug",
      currentLane: "Developing",
      priority: "High",
    });
    mockApi.messages.list.mockResolvedValue([]);
    mockApi.plans.latest.mockResolvedValue(null);
    mockApi.attachments.list.mockResolvedValue([]);
    mockApi.executions.list.mockResolvedValue([]);
  });

  it("renders task not found when task doesn't exist", async () => {
    mockApi.tasks.getByNumber.mockRejectedValue(new Error("Not found"));

    await renderPage(<TaskDetailPage />);
    expect(screen.getByText("reviewPage.taskNotFound")).toBeInTheDocument();
    expect(screen.getByText("1")).toBeInTheDocument();
  });

  it("redirects legacy ULID URLs to the friendly task number", async () => {
    setRoute({ projectId: "proj-1", taskId: "task-1" });

    render(<TaskDetailPage />);
    await waitFor(() =>
      expect(mockI18nRouter.replace).toHaveBeenCalledWith("/p/proj-1/tasks/1")
    );
  });

  it("renders TaskDetailLive when task exists", async () => {
    const { container } = await renderPage(<TaskDetailPage />);
    expect(container.textContent).toContain("TaskDetailLive:Fix login bug");
  });

  it("renders breadcrumb", async () => {
    await renderPage(<TaskDetailPage />);
    expect(screen.getByText("nav.board")).toBeInTheDocument();
    expect(screen.getByText("Fix login bug")).toBeInTheDocument();
  });

  it("fetches related data in parallel when task exists", async () => {
    await renderPage(<TaskDetailPage />);

    expect(mockApi.messages.list).toHaveBeenCalledWith("task-1", "");
    expect(mockApi.plans.latest).toHaveBeenCalledWith("task-1", "");
    expect(mockApi.attachments.list).toHaveBeenCalledWith("task-1", "");
    expect(mockApi.executions.list).toHaveBeenCalledWith("task-1", "");
  });
});
