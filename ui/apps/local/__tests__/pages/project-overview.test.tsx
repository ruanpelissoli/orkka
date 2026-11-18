import { describe, it, expect, beforeEach } from "vitest";
import { screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage, renderPageWithPermissions } from "../render-page";
import ProjectDashboardPage from "@/app/[locale]/(dashboard)/p/[projectId]/page";

describe("ProjectDashboardPage", () => {
  beforeEach(() => {
    setRoute({ projectId: "proj-1" });
    mockApi.projects.get.mockResolvedValue({
      id: "proj-1",
      name: "My Project",
      description: "Project description",
      settings: { maxParallelTasks: 3, sameRepoParallelism: false },
    });
    mockApi.tasks.list.mockResolvedValue([]);
  });

  it("renders project name", async () => {
    await renderPage(<ProjectDashboardPage />);
    expect(screen.getByText("My Project")).toBeInTheDocument();
  });

  it("renders project description", async () => {
    await renderPage(<ProjectDashboardPage />);
    expect(screen.getByText("Project description")).toBeInTheDocument();
  });

  it("renders stat cards", async () => {
    await renderPage(<ProjectDashboardPage />);
    expect(screen.getByText("projectOverview.activeTasks")).toBeInTheDocument();
    expect(screen.getByText("projectOverview.inReview")).toBeInTheDocument();
    expect(screen.getByText("projectOverview.completed")).toBeInTheDocument();
    expect(screen.getByText("projectOverview.activeAgents")).toBeInTheDocument();
  });

  it("renders empty state when no tasks", async () => {
    await renderPage(<ProjectDashboardPage />);
    expect(screen.getByText("projectOverview.noTasksYet")).toBeInTheDocument();
  });

  it("renders recent tasks when tasks exist", async () => {
    mockApi.tasks.list.mockResolvedValue([
      {
        id: "t-1",
        title: "Fix login bug",
        currentLane: "Developing",
        priority: "High",
        repo: { name: "api-repo" },
      },
    ]);

    await renderPage(<ProjectDashboardPage />);
    expect(screen.getByText("Fix login bug")).toBeInTheDocument();
  });

  it("renders quick actions", async () => {
    await renderPageWithPermissions(<ProjectDashboardPage />);
    expect(screen.getByText("projectOverview.quickActions")).toBeInTheDocument();
    expect(screen.getByText("projectOverview.newTask")).toBeInTheDocument();
  });

  it("shows error state when tasks fail to load", async () => {
    mockApi.tasks.list.mockRejectedValue(new Error("API error"));

    await renderPage(<ProjectDashboardPage />);
    expect(screen.getByText("projectOverview.failedToLoadTasks")).toBeInTheDocument();
  });

  it("shows the page error state when the project cannot load", async () => {
    mockApi.projects.get.mockRejectedValueOnce(new Error("gone"));

    await renderPage(<ProjectDashboardPage />);
    expect(screen.getByText("errorPage.somethingWentWrong")).toBeInTheDocument();
  });
});
