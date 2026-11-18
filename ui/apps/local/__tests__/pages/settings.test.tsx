import { describe, it, expect, beforeEach, vi } from "vitest";
import { screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage } from "../render-page";

vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/settings/ProjectSettingsActions",
  () => ({
    ProjectSettingsActions: (props: { project: { name: string }; dangerZone?: boolean }) =>
      props.dangerZone
        ? `DangerZone:${props.project.name}`
        : `SettingsActions:${props.project.name}`,
  })
);

import ProjectSettingsPage from "@/app/[locale]/(dashboard)/p/[projectId]/settings/page";

const routeParams = { projectId: "proj-1" };

describe("ProjectSettingsPage", () => {
  beforeEach(() => {
    setRoute(routeParams);
    mockApi.projects.get.mockResolvedValue({
      id: "proj-1",
      name: "Test Project",
    });
  });

  it("renders general settings heading", async () => {
    await renderPage(<ProjectSettingsPage />);
    expect(screen.getByText("settingsPage.general")).toBeInTheDocument();
  });

  it("renders ProjectSettingsActions with project data", async () => {
    const { container } = await renderPage(<ProjectSettingsPage />);
    expect(container.textContent).toContain("SettingsActions:Test Project");
  });

  it("renders danger zone", async () => {
    const { container } = await renderPage(<ProjectSettingsPage />);
    expect(container.textContent).toContain("DangerZone:Test Project");
  });
});
