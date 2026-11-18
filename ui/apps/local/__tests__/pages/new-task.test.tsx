import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage } from "../render-page";

vi.mock("@/app/[locale]/(dashboard)/p/[projectId]/tasks/new/TaskCreateFormWithModels", () => ({
  TaskCreateFormWithModels: (props: {
    projectId: string;
    repos: unknown[];
    catalog: { id: string }[];
    returnTo?: string;
  }) =>
    `TaskCreateForm:repos=${props.repos.length}:returnTo=${props.returnTo}` +
    `:models=${props.catalog.map((m) => m.id).join("|")}`,
}));

import NewTaskPage from "@/app/[locale]/(dashboard)/p/[projectId]/tasks/new/page";

describe("NewTaskPage", () => {
  beforeEach(() => {
    setRoute({ projectId: "proj-1" });
    mockApi.repos.list.mockResolvedValue([]);
  });

  it("renders create task heading", async () => {
    await renderPage(<NewTaskPage />);
    expect(screen.getByText("taskCreateForm.createTask")).toBeInTheDocument();
  });

  it("renders description text", async () => {
    await renderPage(<NewTaskPage />);
    expect(
      screen.getByText("uiMisc.newTaskSubtitle")
    ).toBeInTheDocument();
  });

  it("renders TaskCreateForm with repos", async () => {
    mockApi.repos.list.mockResolvedValue([
      { id: "r1", name: "frontend" },
      { id: "r2", name: "backend" },
    ]);

    const { container } = await renderPage(<NewTaskPage />);
    expect(container.textContent).toContain("TaskCreateForm:repos=2");
  });

  it("passes the whole chat catalog, runner-only rows included", async () => {
    const { container } = await renderPage(<NewTaskPage />);

    expect(mockApi.models.list).toHaveBeenCalledWith("");
    // copilot/auto is pickable when the user's runner has Copilot signed in — only the client
    // knows, so the page must not drop it.
    expect(container.textContent).toContain("models=claude-sonnet-4-6|copilot/auto");
  });

  it("returns to the board by default after creation", async () => {
    const { container } = await renderPage(<NewTaskPage />);
    expect(container.textContent).toContain("returnTo=/p/proj-1/board");
  });

  it("returns to the tasks page when coming from it", async () => {
    setRoute({ projectId: "proj-1" }, "from=tasks");
    const { container } = await renderPage(<NewTaskPage />);
    expect(container.textContent).toContain("returnTo=/p/proj-1/tasks");
  });

  it("returns to the milestone when coming from one", async () => {
    setRoute({ projectId: "proj-1" }, "from=milestone&milestoneId=ms-1");
    const { container } = await renderPage(<NewTaskPage />);
    expect(container.textContent).toContain("returnTo=/p/proj-1/roadmap/ms-1");
  });

  it("ignores non-whitelisted from values", async () => {
    setRoute({ projectId: "proj-1" }, "from=https%3A%2F%2Fevil.example");
    const { container } = await renderPage(<NewTaskPage />);
    expect(container.textContent).toContain("returnTo=/p/proj-1/board");
  });
});
