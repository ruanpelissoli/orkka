import { describe, it, expect } from "vitest";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { fireEvent, waitFor } from "@testing-library/react";
import { renderWithPermissions, memberMe } from "../render-with-permissions";
import { mockApi } from "../mocks/api";
import { SkillsPage } from "@/components/skills";
import { ToastProvider } from "@/components/ui";
import type { ProjectAgent, Skill } from "@/lib/types";

const agents = [
  { id: "a1", name: "Luna", role: "PM" },
  { id: "a2", name: "Sentinel", role: "CodeReviewer" },
] as ProjectAgent[];

function skill(overrides: Partial<Skill>): Skill {
  return {
    id: "s1",
    projectId: "proj",
    slug: "groom-backlog",
    name: "Groom the backlog",
    description: "Tidy Drafting tasks",
    content: "# Groom",
    isEnabled: true,
    isBuiltIn: true,
    assignedAgentIds: ["a1"],
    createdAt: "2026-08-01T00:00:00Z",
    updatedAt: "2026-08-01T00:00:00Z",
    ...overrides,
  };
}

describe("SkillsPage", () => {
  it("lists skills with slug, built-in badge and assigned agents", () => {
    const { container } = renderWithPermissions(
      <ToastProvider><SkillsPage projectId="proj" agents={agents} initialSkills={[skill({}), skill({ id: "s2", slug: "custom", name: "Custom", isBuiltIn: false, assignedAgentIds: [] })]} /></ToastProvider>
    );
    expect(container.textContent).toContain("/groom-backlog");
    expect(container.textContent).toContain("builtIn");
    expect(container.textContent).toContain("Luna");
    expect(container.textContent).toContain("assignedNone");
  });

  it("offers delete only for custom skills", () => {
    const { getAllByText, queryAllByText } = renderWithPermissions(
      <ToastProvider><SkillsPage projectId="proj" agents={agents} initialSkills={[skill({}), skill({ id: "s2", slug: "custom", name: "Custom", isBuiltIn: false })]} /></ToastProvider>,
      memberMe(["AgentsConfigure"])
    );
    // Two edit buttons (one per card), one delete (custom only).
    expect(getAllByText("edit")).toHaveLength(2);
    expect(queryAllByText("delete")).toHaveLength(1);
  });

  it("toggles enabled through the API and keeps the optimistic state", async () => {
    mockApi.skills.update.mockResolvedValueOnce(skill({ isEnabled: false }));
    const { getByRole } = renderWithPermissions(
      <ToastProvider><SkillsPage projectId="proj" agents={agents} initialSkills={[skill({})]} /></ToastProvider>,
      memberMe(["AgentsConfigure"])
    );
    fireEvent.click(getByRole("switch"));
    await waitFor(() =>
      expect(mockApi.skills.update).toHaveBeenCalledWith("proj", "s1", { isEnabled: false }, "")
    );
  });
});
