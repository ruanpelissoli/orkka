import { describe, it, expect, beforeEach, vi } from "vitest";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { setRoute } from "../mocks/navigation";
import { renderPage } from "../render-page";

vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/repos/RepoFileBrowser",
  () => ({
    RepoFileBrowser: (props: { projectId: string; repos: unknown[] }) =>
      `RepoFileBrowser:repos=${props.repos.length}`,
  })
);

import ReposPage from "@/app/[locale]/(dashboard)/p/[projectId]/repos/page";

describe("ReposPage", () => {
  beforeEach(() => {
    setRoute({ projectId: "proj-1" });
    mockApi.repos.list.mockResolvedValue([]);
  });

  it("renders RepoFileBrowser with repos", async () => {
    mockApi.repos.list.mockResolvedValue([
      { id: "r1", name: "api" },
      { id: "r2", name: "web" },
    ]);

    const { container } = await renderPage(<ReposPage />);
    expect(container.textContent).toContain("RepoFileBrowser:repos=2");
  });
});
