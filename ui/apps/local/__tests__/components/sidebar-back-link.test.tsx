import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/api";

import { Sidebar } from "@/components/layout/Sidebar";
import { ProjectContext, type ProjectContextValue } from "@/components/providers/ProjectProvider";
import type { Project } from "@/lib/types";

function withProjects(projects: Project[]) {
  const value: ProjectContextValue = {
    projects,
    currentProject: null,
    isLoading: false,
    switchProject: () => {},
    refreshProjects: async () => {},
    refreshProject: async () => {},
  };
  return render(
    <ProjectContext.Provider value={value}>
      <Sidebar />
    </ProjectContext.Provider>
  );
}

describe("Sidebar outside a project", () => {
  // The first-run dashboard has no project, so there is nothing to go "back" to.
  it("hides Back to Project when there are no projects", () => {
    withProjects([]);
    expect(screen.queryByText("sidebar.backToProject")).not.toBeInTheDocument();
  });

  it("shows Back to Project once a project exists", () => {
    withProjects([{ id: "p1", slug: "acme", name: "Acme" } as Project]);
    expect(screen.getByText("sidebar.backToProject")).toBeInTheDocument();
  });
});
