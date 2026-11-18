import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";

vi.mock("@/hooks/use-client-api", () => ({
  useClientApi:
    () =>
    <T,>(fn: (token: string) => Promise<T>) =>
      fn("test-token"),
}));

import { PermissionsProvider } from "@/components/providers/PermissionsProvider";
import { ProjectContext, type ProjectContextValue } from "@/components/providers/ProjectProvider";
import { PipelineAutomationSettings } from "@/components/project/PipelineAutomationSettings";
import { ProjectAutomationCard } from "@/components/project/ProjectAutomationCard";
import type { Project } from "@/lib/types";
import { ownerMe } from "../render-with-permissions";

/**
 * The project settings card: the full pipeline knob set under the "Pipeline automation"
 * heading. The intl mock echoes keys, which is what lets these tests pin which rows
 * rendered.
 */
function project(settings: Record<string, unknown> = {}): Project {
  return {
    id: "p1",
    name: "Test",
    slug: "test",
    key: "TST",
    settings: {
      maxParallelTasks: 1,
      sameRepoParallelism: false,
      autoApproveCodeReview: true,
      autoPushOnMerge: false,
      qaEnabled: false,
      qaRunCommand: null,
      manualVerificationEnabled: false,
      showAgentToolCalls: false,
      documentationSyncEnabled: false,
      autoArchiveDoneTasks: false,
      taskRuntimeLimitEnabled: false,
      taskRuntimeLimitMinutes: 30,
      taskSpendLimitEnabled: false,
      taskSpendLimitUsd: 5,
      ...settings,
    },
  } as Project;
}

function renderIn(ui: React.ReactElement) {
  const value = {
    projects: [],
    currentProject: project(),
    isLoading: false,
    switchProject: () => {},
    refreshProjects: async () => {},
    refreshProject: async () => {},
  } as ProjectContextValue;
  return render(
    <PermissionsProvider me={ownerMe}>
      <ProjectContext.Provider value={value}>{ui}</ProjectContext.Provider>
    </PermissionsProvider>
  );
}

describe("PipelineAutomationSettings", () => {
  it("renders the full knob set", () => {
    renderIn(<PipelineAutomationSettings project={project()} />);

    for (const key of [
      "pipelineSettings.parallelTasks",
      "pipelineSettings.sameRepoTitle",
      "pipelineSettings.autoApprovePlanTitle",
      "pipelineSettings.autoApproveTitle",
      "pipelineSettings.qaTitle",
      "pipelineSettings.manualVerificationTitle",
      "pipelineSettings.liveDocsTitle",
      "pipelineSettings.autoArchiveTitle",
      "pipelineSettings.runtimeLimitTitle",
      "pipelineSettings.spendLimitTitle",
      "pipelineSettings.showToolCallsTitle",
    ]) {
      expect(screen.getByText(key)).toBeInTheDocument();
    }
  });
});

/**
 * The task spend limit is US dollars end to end (`taskSpendLimitUsd`, an estimated list-price
 * spend, 0.10 – 10000). The retired `taskSpendLimitCredits` (thousandths of a dollar) must never
 * ride along in the full-replacement settings PATCH.
 */
describe("task spend limit", () => {
  beforeEach(() => {
    mockApi.projects.update.mockReset();
    mockApi.projects.update.mockResolvedValue({});
  });

  function spendToggle() {
    const row = screen.getByText("pipelineSettings.spendLimitTitle").closest("div.flex") as HTMLElement;
    return within(row).getByRole("switch");
  }

  function lastSavedSettings(): Record<string, unknown> {
    const calls = mockApi.projects.update.mock.calls;
    return (calls[calls.length - 1][1] as { settings: Record<string, unknown> }).settings;
  }

  it("turning it on saves the dollar amount in taskSpendLimitUsd, never credits", async () => {
    // A settings bag stored before the rename still carries the old field.
    renderIn(
      <PipelineAutomationSettings project={project({ taskSpendLimitUsd: 7.5, taskSpendLimitCredits: 7500 })} />
    );

    fireEvent.click(spendToggle());

    await waitFor(() => expect(mockApi.projects.update).toHaveBeenCalledTimes(1));
    const saved = lastSavedSettings();
    expect(saved.taskSpendLimitEnabled).toBe(true);
    expect(saved.taskSpendLimitUsd).toBe(7.5);
    expect(saved).not.toHaveProperty("taskSpendLimitCredits");
    expect(screen.getByLabelText("pipelineSettings.spendLimitUsdLabel")).toHaveValue(7.5);
  });

  it("defaults to $5 when the project has no stored limit", async () => {
    renderIn(<PipelineAutomationSettings project={project({ taskSpendLimitUsd: undefined })} />);

    fireEvent.click(spendToggle());

    await waitFor(() => expect(mockApi.projects.update).toHaveBeenCalledTimes(1));
    expect(lastSavedSettings().taskSpendLimitUsd).toBe(5);
  });

  it.each([
    ["12.345", 12.35],
    ["0.01", 0.1],
    ["250000", 10000],
    ["20", 20],
  ])("saves %s typed into the box as %s dollars", async (typed, expected) => {
    renderIn(<PipelineAutomationSettings project={project({ taskSpendLimitEnabled: true })} />);

    const input = screen.getByLabelText("pipelineSettings.spendLimitUsdLabel");
    fireEvent.change(input, { target: { value: typed } });
    fireEvent.blur(input, { target: { value: typed } });

    await waitFor(() => expect(mockApi.projects.update).toHaveBeenCalledTimes(1));
    const saved = lastSavedSettings();
    expect(saved.taskSpendLimitUsd).toBe(expected);
    expect(saved).not.toHaveProperty("taskSpendLimitCredits");
  });

  it("does not save when the box is left on the stored amount", () => {
    renderIn(<PipelineAutomationSettings project={project({ taskSpendLimitEnabled: true })} />);

    const input = screen.getByLabelText("pipelineSettings.spendLimitUsdLabel");
    fireEvent.blur(input, { target: { value: "5" } });

    expect(mockApi.projects.update).not.toHaveBeenCalled();
  });
});

describe("ProjectAutomationCard", () => {
  it("is headed 'pipeline automation'", () => {
    renderIn(<ProjectAutomationCard project={project()} />);
    expect(screen.getByText("settingsPage.pipelineAutomation")).toBeInTheDocument();
  });
});
