import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage, renderPageWithPermissions } from "../render-page";
import { memberMe } from "../render-with-permissions";

// Mock client components
vi.mock("@/components/task/TaskFilterBar", () => ({
  TaskFilterBar: () => "TaskFilterBar",
}));
vi.mock("@/components/task/TaskList", () => ({
  TaskList: (props: { tasks: unknown[]; projectId: string }) =>
    `TaskList:count=${props.tasks.length}`,
}));

import TasksPage from "@/app/[locale]/(dashboard)/p/[projectId]/tasks/page";

const routeParams = { projectId: "proj-1" };

describe("TasksPage", () => {
  beforeEach(() => {
    setRoute(routeParams);
    mockApi.tasks.listFiltered.mockResolvedValue([]);
    mockApi.repos.list.mockResolvedValue([]);
  });

  it("renders tasks heading", async () => {
    await renderPage(<TasksPage />);
    expect(screen.getByText("tasksPage.tasks")).toBeInTheDocument();
  });

  it("renders new task button for users with TasksCreate", async () => {
    await renderPageWithPermissions(<TasksPage />);
    expect(screen.getByText("tasksPage.newTask")).toBeInTheDocument();
  });

  it("hides new task button for members without TasksCreate", async () => {
    await renderPageWithPermissions(<TasksPage />, memberMe());
    expect(screen.queryByText("tasksPage.newTask")).not.toBeInTheDocument();
  });

  it("renders the view switch with Code, Board and List", async () => {
    await renderPage(<TasksPage />);
    // BoardViewSwitch: Code and Board are links away from here; List is the current view.
    // (The intl mock renders component-level keys bare.)
    expect(screen.getByText("code")).toBeInTheDocument();
    expect(screen.getByText("board")).toBeInTheDocument();
    expect(screen.getByText("list")).toBeInTheDocument();
  });

  it("renders TaskList with fetched data", async () => {
    const { container } = await renderPage(<TasksPage />);
    expect(container.textContent).toContain("TaskList:count=0");
  });

  it("calls listFiltered with filter params from search params", async () => {
    setRoute(routeParams, "search=bug&repoId=r1&status=Active");
    await renderPage(<TasksPage />);

    expect(mockApi.tasks.listFiltered).toHaveBeenCalledWith(
      "proj-1",
      expect.objectContaining({
        search: "bug",
        repoId: "r1",
        status: "Active",
      }),
      ""
    );
  });
});
