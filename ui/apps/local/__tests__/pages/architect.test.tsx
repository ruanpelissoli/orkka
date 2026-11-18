import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";

vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/architect/DiagramGallery",
  () => ({
    DiagramGallery: (props: { diagrams: unknown[]; projectId: string }) =>
      `DiagramGallery:count=${props.diagrams.length}`,
  })
);
vi.mock("@/app/[locale]/(dashboard)/p/[projectId]/team/AgentChatDrawer", () => ({
  AgentChatDrawer: () => null,
}));

import ArchitectPage from "@/app/[locale]/(dashboard)/p/[projectId]/architect/page";

describe("ArchitectPage", () => {
  beforeEach(() => {
    setRoute({ projectId: "proj-1" });
    mockApi.diagrams.list.mockReset();
    mockApi.diagrams.list.mockResolvedValue([]);
  });

  it("renders DiagramGallery with fetched diagrams", async () => {
    mockApi.diagrams.list.mockResolvedValue([
      { id: "d1", title: "System Architecture" },
    ]);

    const { container } = render(<ArchitectPage />);
    await waitFor(() => expect(container.textContent).toContain("DiagramGallery:count=1"));
  });

  it("fetches diagrams for the project with the session cookie (empty token)", async () => {
    render(<ArchitectPage />);
    await waitFor(() => expect(mockApi.diagrams.list).toHaveBeenCalledWith("proj-1", ""));
  });

  it("shows the error state with a retry when the diagrams cannot load", async () => {
    mockApi.diagrams.list.mockRejectedValueOnce(new Error("boom"));
    render(<ArchitectPage />);
    expect(await screen.findByText("errorPage.tryAgain")).toBeInTheDocument();

    screen.getByText("errorPage.tryAgain").click();
    await waitFor(() => expect(mockApi.diagrams.list).toHaveBeenCalledTimes(2));
  });
});
