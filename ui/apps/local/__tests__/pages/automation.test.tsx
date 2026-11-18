import { describe, it, expect, beforeEach } from "vitest";
import { screen, fireEvent, waitFor, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";
import { ToastProvider } from "@/components/ui";
import type { Project, ProjectAgent, Schedule, Skill, UpsertScheduleRequest } from "@/lib/types";

import { AutomationPage } from "@/components/automation";

// The intl mock echoes keys, so a starter's schedule name comes back as
// "templates.<key>.scheduleName" — which is exactly what the installed-check matches on.
const project = {
  id: "proj-1",
  slug: "my-project",
  name: "My Project",
  settings: { automationEnabled: true },
} as unknown as Project;

const agents = [
  { id: "agent-pm", projectId: "proj-1", name: "Luna", role: "PM", isDefault: true },
  { id: "agent-dev", projectId: "proj-1", name: "Atlas", role: "BackendDev", isDefault: false },
] as unknown as ProjectAgent[];

const skills = [
  { id: "skill-groom", projectId: "proj-1", slug: "groom-backlog", name: "Groom the backlog", isEnabled: true },
] as unknown as Skill[];

function scheduleFrom(body: UpsertScheduleRequest, id: string): Schedule {
  return {
    id,
    projectId: project.id,
    runCount: 0,
    consecutiveFailures: 0,
    createdByUserId: "u1",
    createdAt: "2026-08-20T00:00:00Z",
    updatedAt: "2026-08-20T00:00:00Z",
    lastRunAt: null,
    lastRunStatus: null,
    nextRunAt: null,
    conversationId: null,
    fireAt: null,
    agentId: null,
    skillId: null,
    message: null,
    autopilotCapUsd: null,
    timeZoneId: "UTC",
    ...body,
  } as unknown as Schedule;
}

function renderPage(initialSchedules: Schedule[] = []) {
  return renderWithPermissions(
    <ToastProvider>
      <AutomationPage project={project} initialSchedules={initialSchedules} agents={agents} skills={skills} />
    </ToastProvider>
  );
}

describe("AutomationPage", () => {
  beforeEach(() => {
    mockApi.schedules.create.mockReset();
    mockApi.schedules.list.mockReset();
    mockApi.schedules.list.mockResolvedValue([]);
    let n = 0;
    mockApi.schedules.create.mockImplementation(
      async (_projectId: string, body: UpsertScheduleRequest) => scheduleFrom(body, `sched-${++n}`)
    );
  });

  it("explains the loop and separates playbooks from Autopilot windows", () => {
    renderPage();

    expect(screen.getByTestId("how-it-works-card")).toBeInTheDocument();
    // The capability boundary is the thing the page previously never said.
    expect(screen.getByText("howBoundary")).toBeInTheDocument();
    expect(screen.getByTestId("playbooks-section")).toBeInTheDocument();
    expect(screen.getByTestId("windows-section")).toBeInTheDocument();
    expect(screen.getByTestId("guardrails")).toBeInTheDocument();
  });

  it("installs a starter playbook switched off, bound to the matching agent and skill", async () => {
    renderPage();

    const card = screen.getByText("templates.groomBacklog.title").closest("li") as HTMLElement;
    fireEvent.click(within(card).getByRole("button", { name: "templateAdd" }));

    await waitFor(() => expect(mockApi.schedules.create).toHaveBeenCalledTimes(1));
    expect(mockApi.schedules.create).toHaveBeenCalledWith(
      "proj-1",
      expect.objectContaining({
        kind: "Playbook",
        cronExpression: "0 9 * * 1",
        agentId: "agent-pm",
        skillId: "skill-groom",
        // Nothing a starter creates may fire before the user has looked at it.
        isEnabled: false,
      }),
      ""
    );

    // Installed starters drop out of the catalogue and appear as an ordinary row.
    await waitFor(() => expect(screen.queryByText("templates.groomBacklog.title")).not.toBeInTheDocument());
    expect(within(screen.getByTestId("playbook-list")).getByText("templates.groomBacklog.scheduleName")).toBeInTheDocument();
  });

  it("installs the night shift as a start/stop pair", async () => {
    renderPage();

    const card = screen.getByText("templates.nightShift.title").closest("li") as HTMLElement;
    fireEvent.click(within(card).getByRole("button", { name: "templateAdd" }));

    await waitFor(() => expect(mockApi.schedules.create).toHaveBeenCalledTimes(2));
    const kinds = mockApi.schedules.create.mock.calls.map((c) => (c[1] as UpsertScheduleRequest).kind);
    expect(kinds).toEqual(["AutopilotStart", "AutopilotStop"]);
    expect((mockApi.schedules.create.mock.calls[0][1] as UpsertScheduleRequest).autopilotCapUsd).toBe(20);
  });

  it("hides a starter whose schedule already exists", () => {
    renderPage([
      scheduleFrom(
        { name: "templates.releaseNotes.scheduleName", kind: "Playbook", cronExpression: "0 17 * * 5", isEnabled: true },
        "existing-1"
      ),
    ]);

    expect(screen.queryByText("templates.releaseNotes.title")).not.toBeInTheDocument();
    expect(screen.getByText("templates.groomBacklog.title")).toBeInTheDocument();
  });

  it("warns when an Autopilot start has no matching stop", () => {
    renderPage([
      scheduleFrom(
        { name: "Night shift", kind: "AutopilotStart", cronExpression: "0 22 * * *", isEnabled: true },
        "existing-2"
      ),
    ]);

    expect(screen.getByTestId("unpaired-window")).toBeInTheDocument();
  });
});
