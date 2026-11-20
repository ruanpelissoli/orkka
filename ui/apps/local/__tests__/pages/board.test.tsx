import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage, renderPageWithPermissions } from "../render-page";

// Mock the TaskBoard client component
vi.mock("@/components/board", () => ({
  TaskBoard: (props: { boardData: { lanes: unknown[] }; projectId: string }) =>
    `TaskBoard:lanes=${props.boardData.lanes.length}`,
  // Client component with its own SignalR subscription — stubbed so this test stays about
  // the page's composition.
  AutopilotToggle: (props: { initialStatus: { running: boolean } }) =>
    `AutopilotToggle:running=${props.initialStatus.running}`,
  BoardViewSwitch: (props: { active: string }) => `BoardViewSwitch:active=${props.active}`,
  TicketSourceStatusLine: (props: { config: { scope: string } | null }) =>
    `TicketSourceStatusLine:${props.config?.scope ?? "none"}`,
  ImportTicketButton: (props: { projects: { slug: string }[] }) =>
    `ImportTicketButton:${props.projects.map((p) => p.slug).join(",")}`,
}));

vi.mock("@/components/code-view", () => ({
  CodeView: (props: { boardData: { lanes: unknown[] } }) =>
    `CodeView:lanes=${props.boardData.lanes.length}`,
}));

// Client component with its own hooks and toast context — covered by its own test.
vi.mock("@/components/runner/ProviderMismatchBanner", () => ({
  ProviderMismatchBanner: (props: { agents: unknown[]; compact?: boolean }) =>
    `ProviderMismatchBanner:agents=${props.agents.length}:compact=${props.compact ?? false}`,
}));

import BoardPage from "@/app/[locale]/(dashboard)/p/[projectId]/board/page";

/** The kanban Board is the default; `view=code` selects the Code View. */
const board = () => setRoute({ projectId: "proj-1" });
const code = () => setRoute({ projectId: "proj-1" }, "view=code");

describe("BoardPage", () => {
  beforeEach(() => {
    board();
    mockApi.board.get.mockResolvedValue({ lanes: [] });
  });

  it("renders the kanban by default", async () => {
    const { container } = await renderPage(<BoardPage />);
    expect(screen.getAllByText("boardPage.board").length).toBeGreaterThanOrEqual(1);
    expect(container.textContent).toContain("TaskBoard:lanes=0");
    expect(container.textContent).toContain("BoardViewSwitch:active=board");
    expect(container.textContent).not.toContain("CodeView:");
  });

  it("renders the Code view with ?view=code", async () => {
    code();
    const { container } = await renderPage(<BoardPage />);
    expect(screen.getAllByText("boardPage.code").length).toBeGreaterThanOrEqual(1);
    expect(container.textContent).toContain("CodeView:lanes=0");
    expect(container.textContent).toContain("BoardViewSwitch:active=code");
    expect(container.textContent).not.toContain("TaskBoard:");
  });

  it("renders new task button for users with TasksCreate", async () => {
    code();
    await renderPageWithPermissions(<BoardPage />);
    expect(screen.getByText("boardPage.newTask")).toBeInTheDocument();
  });

  it("hides new task button without the permissions provider (fail-closed)", async () => {
    code();
    await renderPage(<BoardPage />);
    expect(screen.queryByText("boardPage.newTask")).not.toBeInTheDocument();
  });

  it("shows task count", async () => {
    code();
    mockApi.board.get.mockResolvedValue({
      lanes: [
        { key: "Drafting", label: "Drafting", order: 0, tasks: [{ id: "t1" }, { id: "t2" }] },
      ],
    });

    await renderPage(<BoardPage />);
    expect(screen.getByText("boardPage.tasks")).toBeInTheDocument();
  });

  it("renders TaskBoard with board data", async () => {
    const { container } = await renderPage(<BoardPage />);
    expect(container.textContent).toContain("TaskBoard:lanes=0");
  });

  it("shows the ticket-source line only when the project has a tracker", async () => {
    mockApi.trackers.getProjectConfig.mockResolvedValueOnce({ scope: "SHOP" });
    const withSource = await renderPageWithPermissions(<BoardPage />);
    expect(withSource.container.textContent).toContain("TicketSourceStatusLine:SHOP");
    // Importing a ticket by its key needs a ticket source to read it from.
    expect(withSource.container.textContent).toContain("ImportTicketButton:");
    withSource.unmount();

    mockApi.trackers.getProjectConfig.mockResolvedValueOnce(null);
    const without = await renderPageWithPermissions(<BoardPage />);
    expect(without.container.textContent).not.toContain("TicketSourceStatusLine");
    expect(without.container.textContent).not.toContain("ImportTicketButton");
  });

  it("shows Finish setup while the project's setup was left before Start", async () => {
    mockApi.projects.get.mockResolvedValueOnce({ id: "proj-1", slug: "shop", name: "Shop", setupCompletedAt: null });
    const unfinished = await renderPage(<BoardPage />);
    expect(screen.getByTestId("finish-setup-banner")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "action" })).toHaveAttribute("href", "/p/shop/setup");
    unfinished.unmount();

    mockApi.projects.get.mockResolvedValueOnce({
      id: "proj-1", slug: "shop", name: "Shop", setupCompletedAt: "2026-11-19T00:00:00Z",
    });
    await renderPage(<BoardPage />);
    expect(screen.queryByTestId("finish-setup-banner")).toBeNull();
  });
});
