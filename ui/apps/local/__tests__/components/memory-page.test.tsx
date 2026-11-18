import { describe, it, expect, beforeEach } from "vitest";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { fireEvent, waitFor } from "@testing-library/react";
import { renderWithPermissions, memberMe } from "../render-with-permissions";
import { mockApi } from "../mocks/api";
import { MemoryPage } from "@/components/memory";
import { ToastProvider } from "@/components/ui";
import type { ProjectAgent, ProjectMemory } from "@/lib/types";

const agents = [{ id: "a1", name: "Luna", role: "PM" }] as ProjectAgent[];

function memory(overrides: Partial<ProjectMemory>): ProjectMemory {
  return {
    id: "m1",
    projectId: "p1",
    agentId: null,
    agentName: null,
    type: "Decision",
    title: "Postgres over Mongo",
    content: "Relational integrity matters here.",
    source: "User",
    createdByUserId: "user_1",
    taskId: null,
    isPinned: true,
    isArchived: false,
    useCount: 3,
    lastUsedAt: null,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    ...overrides,
  };
}

function renderPage(me = undefined as Parameters<typeof renderWithPermissions>[1]) {
  return renderWithPermissions(
    <ToastProvider>
      <MemoryPage projectId="p1" agents={agents} />
    </ToastProvider>,
    me
  );
}

describe("MemoryPage", () => {
  beforeEach(() => {
    mockApi.memories.list.mockReset();
    mockApi.memories.create.mockReset();
    mockApi.memories.update.mockReset();
    mockApi.memories.preview.mockReset();
  });

  it("lists memories with type, scope and pinned state; hides edit controls without AgentsConfigure", async () => {
    mockApi.memories.list.mockResolvedValue({
      items: [memory({}), memory({ id: "m2", type: "Mistake", title: "Forgot migration", agentId: "a1", agentName: "Luna", isPinned: false, source: "Agent" })],
      nextCursor: null,
      total: 2,
    });
    const owner = renderPage();
    await waitFor(() => expect(owner.getAllByTestId("memory-row")).toHaveLength(2));
    expect(owner.getByText("Postgres over Mongo")).toBeTruthy();
    expect(owner.getAllByText("pinned").length).toBeGreaterThan(0);
    expect(owner.getByText(/privateTo/)).toBeTruthy();
    expect(owner.getAllByTestId("memory-pin")).toHaveLength(2);
    owner.unmount();

    const member = renderPage(memberMe(["TasksCreate"]));
    await waitFor(() => expect(member.getAllByTestId("memory-row")).toHaveLength(2));
    expect(member.queryByTestId("memory-add")).toBeNull();
    expect(member.queryAllByTestId("memory-pin")).toHaveLength(0);
  });

  it("creates a pinned convention scoped to the team", async () => {
    mockApi.memories.list.mockResolvedValue({ items: [], nextCursor: null, total: 0 });
    mockApi.memories.create.mockResolvedValue(memory({ id: "new" }));
    const { getByTestId, getByPlaceholderText, getByLabelText, container } = renderPage();
    await waitFor(() => expect(getByTestId("memory-add")).toBeTruthy());

    fireEvent.click(getByTestId("memory-add"));
    fireEvent.change(getByPlaceholderText("titlePlaceholder"), { target: { value: "Use pnpm" } });
    fireEvent.change(getByPlaceholderText("contentPlaceholder"), { target: { value: "Never npm install." } });
    // The editor's type select is the last "fieldType" select rendered inside the modal.
    const selects = [...container.ownerDocument.querySelectorAll("select")];
    const typeSelect = selects.find((s) => [...s.options].some((o) => o.value === "Convention") && s.closest('[role="dialog"]')) ?? selects[selects.length - 2]!;
    fireEvent.change(typeSelect, { target: { value: "Convention" } });
    fireEvent.click(getByLabelText("pinLabel"));
    fireEvent.click(getByTestId("memory-save"));

    await waitFor(() => expect(mockApi.memories.create).toHaveBeenCalledTimes(1));
    const [projectId, body] = mockApi.memories.create.mock.calls[0]!;
    expect(projectId).toBe("p1");
    expect(body).toMatchObject({ title: "Use pnpm", content: "Never npm install.", type: "Convention", agentId: null, isPinned: true, isArchived: false });
  });

  it("pin toggle updates the row and search re-queries with q", async () => {
    mockApi.memories.list.mockResolvedValue({ items: [memory({ isPinned: false })], nextCursor: null, total: 1 });
    mockApi.memories.update.mockResolvedValue(memory({ isPinned: true }));
    const { getByTestId, getByPlaceholderText } = renderPage();
    await waitFor(() => expect(getByTestId("memory-pin")).toBeTruthy());

    fireEvent.click(getByTestId("memory-pin"));
    await waitFor(() => expect(mockApi.memories.update).toHaveBeenCalledTimes(1));
    expect(mockApi.memories.update.mock.calls[0]![2]).toMatchObject({ isPinned: true });

    fireEvent.change(getByPlaceholderText("searchPlaceholder"), { target: { value: "postgres" } });
    await waitFor(() => expect(mockApi.memories.list.mock.calls.some((c) => c[1]?.q === "postgres")).toBe(true));
  });

  it("retrieval preview calls the preview endpoint for the chosen agent", async () => {
    mockApi.memories.list.mockResolvedValue({ items: [], nextCursor: null, total: 0 });
    mockApi.memories.preview.mockResolvedValue([memory({})]);
    const { getByTestId, getByPlaceholderText, container } = renderPage();
    await waitFor(() => expect(getByTestId("memory-preview-open")).toBeTruthy());
    fireEvent.click(getByTestId("memory-preview-open"));
    fireEvent.change(getByPlaceholderText("previewPlaceholder"), { target: { value: "add oauth login" } });
    const selects = [...container.ownerDocument.querySelectorAll("select")];
    const agentSelect = selects.find((s) => [...s.options].some((o) => o.textContent === "Luna"))!;
    fireEvent.change(agentSelect, { target: { value: "a1" } });
    fireEvent.click(getByTestId("memory-preview-run"));
    await waitFor(() => expect(mockApi.memories.preview).toHaveBeenCalledWith("p1", "add oauth login", "a1", ""));
    await waitFor(() => expect(getByTestId("memory-preview-results").textContent).toContain("Postgres over Mongo"));
  });
});
