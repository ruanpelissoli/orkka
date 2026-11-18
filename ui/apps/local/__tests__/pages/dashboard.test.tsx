import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { mockI18nRouter } from "../mocks/i18n-navigation";
import { renderPage } from "../render-page";
import DashboardPage from "@/app/[locale]/(dashboard)/dashboard/page";

function setLastProject(value: string | null) {
  document.cookie =
    value === null
      ? "orkka-last-project=; path=/; max-age=0"
      : `orkka-last-project=${value}; path=/`;
}

describe("DashboardPage", () => {
  beforeEach(() => {
    setLastProject(null);
    mockApi.projects.list.mockResolvedValue([]);
    mockI18nRouter.replace.mockClear();
  });

  it("shows the first-run empty state, linking to project creation, when no projects", async () => {
    await renderPage(<DashboardPage />);
    expect(screen.getByText("title")).toBeInTheDocument();
    expect(screen.getByRole("link")).toHaveAttribute("href", "/projects/new");
    expect(mockI18nRouter.replace).not.toHaveBeenCalled();
  });

  it("redirects to first project when projects exist", async () => {
    mockApi.projects.list.mockResolvedValue([{ id: "01ABC", slug: "proj-1" }]);

    render(<DashboardPage />);
    await waitFor(() => expect(mockI18nRouter.replace).toHaveBeenCalledWith("/p/proj-1"));
  });

  it("redirects to last project from cookie", async () => {
    setLastProject("proj-2");
    mockApi.projects.list.mockResolvedValue([
      { id: "01ABC", slug: "proj-1" },
      { id: "01DEF", slug: "proj-2" },
    ]);

    render(<DashboardPage />);
    await waitFor(() => expect(mockI18nRouter.replace).toHaveBeenCalledWith("/p/proj-2"));
  });

  it("falls back to first project when cookie project not found", async () => {
    setLastProject("proj-gone");
    mockApi.projects.list.mockResolvedValue([{ id: "01ABC", slug: "proj-1" }]);

    render(<DashboardPage />);
    await waitFor(() => expect(mockI18nRouter.replace).toHaveBeenCalledWith("/p/proj-1"));
  });

  it("shows the first-run state rather than an error page when the API fails", async () => {
    mockApi.projects.list.mockRejectedValue(new Error("API unavailable"));

    await renderPage(<DashboardPage />);
    expect(screen.getByText("createProject")).toBeInTheDocument();
    expect(mockI18nRouter.replace).not.toHaveBeenCalled();
  });
});
