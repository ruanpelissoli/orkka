import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi, mockInvalidateApiCache } from "../mocks/api";
import { getClientWorkspace, setClientWorkspace } from "@/lib/workspace";
import type { Workspace } from "@/lib/types";

const hardNavigate = vi.fn();
vi.mock("@/lib/hard-navigate", () => ({ hardNavigate: (url: string) => hardNavigate(url) }));

import { WorkspaceProvider } from "@/components/workspace/WorkspaceProvider";
import { WorkspaceSwitcher } from "@/components/workspace/WorkspaceSwitcher";

const WS_A = "01JAAAAAAAAAAAAAAAAAAAAAAA";
const WS_B = "01JBBBBBBBBBBBBBBBBBBBBBBB";

const workspaces: Workspace[] = [
  { orgId: WS_A, name: "Acme (day job)", slug: "acme", role: "Owner", isActive: true },
  { orgId: WS_B, name: "Globex (freelance)", slug: "globex", role: "Member", isActive: false },
];

function show(currentId = WS_A, stale = false) {
  return render(
    <WorkspaceProvider workspaces={workspaces} currentId={currentId} stale={stale}>
      <WorkspaceSwitcher />
    </WorkspaceProvider>
  );
}

describe("WorkspaceSwitcher", () => {
  beforeEach(() => {
    hardNavigate.mockReset();
    mockInvalidateApiCache.mockClear();
    mockApi.workspaces.setActive.mockReset();
    mockApi.workspaces.setActive.mockResolvedValue(undefined);
    mockApi.workspaces.create.mockReset();
    mockApi.runner.status.mockReset();
    mockApi.runner.status.mockImplementation((_token: string, workspaceId?: string) =>
      Promise.resolve({ online: workspaceId === undefined })
    );
  });

  afterEach(() => setClientWorkspace(null));

  it("shows the current workspace and adopts it as the cookie on first visit", async () => {
    show();

    expect(screen.getByTestId("workspace-switcher")).toHaveTextContent("Acme (day job)");
    await waitFor(() => expect(getClientWorkspace()).toBe(WS_A));
  });

  it("lists every workspace with its role, the current one checked", async () => {
    show();

    fireEvent.click(screen.getByRole("button", { expanded: false }));
    const current = screen.getByTestId(`workspace-option-${WS_A}`);
    const other = screen.getByTestId(`workspace-option-${WS_B}`);
    expect(current).toHaveAttribute("aria-checked", "true");
    expect(other).toHaveAttribute("aria-checked", "false");
    expect(within(current).getByText("roleOwner")).toBeInTheDocument();
    expect(within(other).getByText("roleMember")).toBeInTheDocument();
  });

  it("switching sets the cookie, the active workspace, clears cached data and reloads", async () => {
    setClientWorkspace(WS_A);
    show();

    fireEvent.click(screen.getByRole("button", { expanded: false }));
    fireEvent.click(screen.getByTestId(`workspace-option-${WS_B}`));

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledWith("/en/dashboard"));
    expect(getClientWorkspace()).toBe(WS_B);
    expect(mockInvalidateApiCache).toHaveBeenCalled();
    expect(mockApi.workspaces.setActive).toHaveBeenCalledWith(WS_B, "");
  });

  it("fetches the current runner once, and the others only when the menu opens", async () => {
    show();

    await waitFor(() => expect(mockApi.runner.status).toHaveBeenCalledTimes(1));
    // The current workspace is asked without an override — it is the request's own workspace.
    expect(mockApi.runner.status).toHaveBeenCalledWith("");

    fireEvent.click(screen.getByRole("button", { expanded: false }));
    await waitFor(() => expect(mockApi.runner.status).toHaveBeenCalledWith("", WS_B));
    expect(mockApi.runner.status).toHaveBeenCalledTimes(2);
    expect(await screen.findAllByLabelText("runnerOnline")).not.toHaveLength(0);
  });

  it("creates a workspace by name and opens its dashboard", async () => {
    const WS_C = "01JCCCCCCCCCCCCCCCCCCCCCCC";
    mockApi.workspaces.create.mockResolvedValue({
      orgId: WS_C,
      name: "Initech",
      slug: "initech",
      role: "Owner",
      isActive: true,
    });
    show();

    fireEvent.click(screen.getByRole("button", { expanded: false }));
    fireEvent.click(screen.getByRole("menuitem", { name: "newWorkspace" }));
    fireEvent.change(screen.getByLabelText("nameLabel"), { target: { value: "Initech" } });
    fireEvent.click(screen.getByRole("button", { name: "create" }));

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledWith("/en/dashboard"));
    expect(mockApi.workspaces.create).toHaveBeenCalledWith("Initech", "");
    expect(getClientWorkspace()).toBe(WS_C);
  });

  it("re-points a stale cookie and reloads", async () => {
    setClientWorkspace("01JZZZZZZZZZZZZZZZZZZZZZZZ");
    show(WS_A, true);

    await waitFor(() => expect(hardNavigate).toHaveBeenCalledWith("/en/dashboard"));
    expect(getClientWorkspace()).toBe(WS_A);
  });
});
