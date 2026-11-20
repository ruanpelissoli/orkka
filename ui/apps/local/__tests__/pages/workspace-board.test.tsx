import { describe, it, expect, beforeEach, vi } from "vitest";
import { fireEvent, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { mockUpdateSearchParams, setRoute } from "../mocks/navigation";
import { mockI18nRouter } from "../mocks/i18n-navigation";
import { renderPage, renderPageWithPermissions } from "../render-page";

// The board itself is covered by its own tests; here it reports what the page handed it.
vi.mock("@/components/board", () => ({
  TaskBoard: (props: {
    boardData: { lanes: unknown[] };
    workspace?: { project: string | null; projectUlids: readonly string[] };
  }) =>
    `TaskBoard:lanes=${props.boardData.lanes.length}:project=${props.workspace?.project ?? "all"}:groups=${props.workspace?.projectUlids.join(",")}`,
  ImportTicketButton: (props: { projects: { slug: string }[] }) =>
    `ImportTicketButton:${props.projects.map((p) => p.slug).join(",")}`,
}));

import WorkspaceBoardPage from "@/app/[locale]/(dashboard)/board/page";

const SHOP = { id: "01SHOP", slug: "shop", name: "Shop Front", key: "SHOP" };
const BILL = { id: "01BILL", slug: "billing", name: "Billing API", key: "BILL" };
const LANES = { lanes: [{ key: "drafting", label: "Drafting", order: 0, tasks: [] }] };

describe("WorkspaceBoardPage", () => {
  beforeEach(() => {
    setRoute({});
    mockApi.projects.list.mockResolvedValue([SHOP, BILL]);
    mockApi.board.workspace.mockResolvedValue(LANES);
    mockUpdateSearchParams.mockClear();
    mockI18nRouter.push.mockClear();
  });

  it("shows every project's tasks, live over every project's board group", async () => {
    const { container } = await renderPage(<WorkspaceBoardPage />);
    expect(screen.getByText("workspaceBoard.title")).toBeInTheDocument();
    expect(mockApi.board.workspace).toHaveBeenCalledWith(null, "");
    expect(container.textContent).toContain("TaskBoard:lanes=1:project=all:groups=01SHOP,01BILL");
    const filter = screen.getByTestId("workspace-board-project-filter") as HTMLSelectElement;
    expect(filter.value).toBe("");
    expect(Array.from(filter.options).map((o) => o.textContent)).toEqual([
      "workspaceBoard.allProjects",
      "Shop Front",
      "Billing API",
    ]);
    expect(screen.queryByTestId("workspace-board-open-project")).toBeNull();
  });

  it("narrows to the project in ?project=, and links to that project's own board", async () => {
    setRoute({}, "project=billing");
    const { container } = await renderPage(<WorkspaceBoardPage />);
    expect(mockApi.board.workspace).toHaveBeenCalledWith("billing", "");
    expect(container.textContent).toContain("project=billing:groups=01BILL");
    expect((screen.getByTestId("workspace-board-project-filter") as HTMLSelectElement).value).toBe("billing");
    expect(screen.getByTestId("workspace-board-open-project")).toHaveAttribute("href", "/p/billing/board");
  });

  it("keeps the filter in the URL", async () => {
    await renderPage(<WorkspaceBoardPage />);
    fireEvent.change(screen.getByTestId("workspace-board-project-filter"), { target: { value: "shop" } });
    expect(mockUpdateSearchParams).toHaveBeenCalled();
    const params = new URLSearchParams("task=BILL-3");
    mockUpdateSearchParams.mock.calls.at(-1)![0](params);
    expect(params.get("project")).toBe("shop");
    expect(params.get("task")).toBeNull();
  });

  it("falls back to every project, and says so, when ?project= is not one of this workspace's", async () => {
    setRoute({}, "project=elsewhere");
    const { container } = await renderPage(<WorkspaceBoardPage />);
    expect(mockApi.board.workspace).toHaveBeenCalledWith(null, "");
    expect(container.textContent).toContain("project=all");
    expect(screen.getByTestId("workspace-board-unknown-project")).toHaveTextContent("workspaceBoard.unknownProject");
  });

  it("asks which project a new task goes to when every project is shown", async () => {
    await renderPageWithPermissions(<WorkspaceBoardPage />);
    fireEvent.click(screen.getByTestId("workspace-board-new-task"));
    expect(screen.getByText("workspaceBoard.newTaskIn")).toBeInTheDocument();
    fireEvent.click(screen.getAllByText("Billing API").find((el) => el.tagName !== "OPTION")!);
    expect(mockI18nRouter.push).toHaveBeenCalledWith("/p/billing/tasks/new?from=board");
  });

  it("creates a new task straight in the project the board is narrowed to", async () => {
    setRoute({}, "project=shop");
    await renderPageWithPermissions(<WorkspaceBoardPage />);
    expect(screen.queryByTestId("workspace-board-new-task")).toBeNull();
    expect(screen.getByText("boardPage.newTask").closest("a")).toHaveAttribute("href", "/p/shop/tasks/new?from=board");
  });

  it("hides New task without TasksCreate (fail-closed)", async () => {
    await renderPage(<WorkspaceBoardPage />);
    expect(screen.queryByText("boardPage.newTask")).toBeNull();
  });

  it("shows the first-run state when the workspace has no projects", async () => {
    mockApi.projects.list.mockResolvedValue([]);
    mockApi.board.workspace.mockClear();
    await renderPage(<WorkspaceBoardPage />);
    expect(screen.getByTestId("workspace-board-empty")).toBeInTheDocument();
    expect(screen.getByText("createProject").closest("a")).toHaveAttribute("href", "/projects/new");
    expect(mockApi.board.workspace).not.toHaveBeenCalled();
  });
});
