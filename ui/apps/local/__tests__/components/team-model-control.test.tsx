import { describe, it, expect, beforeEach, vi } from "vitest";
import { fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { renderWithPermissions, memberMe } from "../render-with-permissions";

vi.mock("@/lib/actions", () => ({
  setAgentsModel: vi.fn(),
}));

import { TeamModelControl } from "@/app/[locale]/(dashboard)/p/[projectId]/team/TeamModelControl";
import { ToastProvider } from "@/components/ui";
import { setAgentsModel } from "@/lib/actions";
import type { AiModel, ProjectAgent } from "@/lib/types";

function model(overrides: Partial<AiModel> & { id: string }): AiModel {
  return {
    name: overrides.id,
    provider: "Anthropic",
    kind: "Chat",
    description: "",
    supportsMcp: true,
    isEnabled: true,
    requiresRunner: false,
    sortOrder: 0,
    ...overrides,
  };
}

const models: AiModel[] = [
  model({ id: "claude-sonnet-5", name: "Claude Sonnet 5", sortOrder: 10 }),
  model({ id: "gpt-5.6-sol", name: "GPT-5.6 Sol", provider: "OpenAI", supportsMcp: false, sortOrder: 20 }),
  model({ id: "copilot/auto", name: "Copilot Auto", provider: "GitHubCopilot",
          requiresRunner: true, supportsMcp: false, sortOrder: 30 }),
  model({ id: "copilot/claude-sonnet-5", name: "Copilot Claude Sonnet 5", provider: "GitHubCopilot",
          requiresRunner: true, supportsMcp: false, sortOrder: 40 }),
];

const agents = [
  { id: "a1", role: "FullStackDev", model: "claude-sonnet-5" },
  { id: "a2", role: "QA", model: "claude-sonnet-5" },
] as ProjectAgent[];

const row = (provider: string, ready: boolean) => ({
  provider,
  cliFound: ready,
  authenticated: ready,
  models: null,
  detail: null,
});

// Claude + Copilot signed in, Codex not.
const runner = {
  online: true,
  machineName: "laptop",
  daemonVersion: "1.0.0",
  connectedAt: null,
  lastSeenAt: null,
  providers: [row("claude", true), row("codex", false), row("copilot", true)],
  lastKnownProviders: null,
  lastKnownAt: null,
  lastKnownMachineName: null,
  hasActiveTokens: true,
};

function renderControl(me?: Parameters<typeof renderWithPermissions>[1]) {
  return renderWithPermissions(
    <ToastProvider>
      <TeamModelControl agents={agents} models={models} projectId="proj-1" />
    </ToastProvider>,
    me
  );
}

const optionValues = (container: HTMLElement) =>
  [...container.querySelectorAll<HTMLOptionElement>("select[name='teamModel'] option")].map(
    (o) => o.value
  );

describe("TeamModelControl", () => {
  beforeEach(() => {
    vi.mocked(setAgentsModel).mockReset();
    mockApi.runner.status.mockResolvedValue(runner);
  });

  it("lists only the models the runner has ready", async () => {
    const { container } = renderControl();

    await waitFor(() => expect(optionValues(container).length).toBeGreaterThan(0));
    expect(optionValues(container)).toEqual([
      "claude-sonnet-5",
      "copilot/auto",
      "copilot/claude-sonnet-5",
    ]);
  });

  it("applies the picked model to every agent only after the confirm", async () => {
    vi.mocked(setAgentsModel).mockResolvedValue({
      updatedCount: 1,
      unchangedCount: 0,
      skippedQaCount: 1,
    });
    const { container, getByText, findByText, queryByText } = renderControl();
    await waitFor(() => expect(optionValues(container).length).toBeGreaterThan(0));

    fireEvent.change(container.querySelector("select[name='teamModel']")!, {
      target: { value: "copilot/auto" },
    });
    fireEvent.click(getByText("apply"));

    // Nothing is written until the dialog is confirmed; the dialog says QA keeps its model.
    expect(setAgentsModel).not.toHaveBeenCalled();
    await findByText("confirmTitle");
    getByText("confirmQaNote");

    fireEvent.click(getByText("confirm"));

    await waitFor(() => expect(setAgentsModel).toHaveBeenCalledWith("proj-1", "copilot/auto"));
    await waitFor(() => expect(queryByText("confirmTitle")).toBeNull());
  });

  it("does not warn about QA for a model with browser tools", async () => {
    const { container, getByText, findByText, queryByText } = renderControl();
    await waitFor(() => expect(optionValues(container).length).toBeGreaterThan(0));

    fireEvent.click(getByText("apply"));
    await findByText("confirmTitle");

    expect(queryByText("confirmQaNote")).toBeNull();
  });

  it("says so when no model is ready on the runner", async () => {
    mockApi.runner.status.mockResolvedValue({
      ...runner,
      providers: [row("claude", false), row("codex", false), row("copilot", false)],
    });
    const { findByText, container } = renderControl();

    await findByText("noReadyModels");
    expect(container.querySelector("select[name='teamModel']")).toBeNull();
  });

  it("is hidden without the AgentsConfigure permission", async () => {
    const { container } = renderControl(memberMe([]));

    await waitFor(() => expect(mockApi.runner.status).toHaveBeenCalled());
    expect(container.querySelector("[data-testid='team-model-control']")).toBeNull();
  });
});
