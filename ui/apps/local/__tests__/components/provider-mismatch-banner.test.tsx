import { describe, it, expect, beforeEach, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { mockApi } from "../mocks/api";

import { ProviderMismatchBanner } from "@/components/runner/ProviderMismatchBanner";
import { ToastProvider } from "@/components/ui";
import type { ProjectAgent } from "@/lib/types";

function makeAgent(id: string, role: string, model: string): ProjectAgent {
  return {
    id,
    projectId: "proj-1",
    repoIds: [],
    name: id,
    role,
    avatar: id,
    model,
    systemPrompt: "",
    userInstructions: "",
    isDefault: true,
    totalTokensIn: 0,
    totalTokensOut: 0,
    billed: null,
    totalCostUsd: 0,
    tasksCompleted: 0,
    tasksFailed: 0,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
  } as ProjectAgent;
}

// A claude-seeded pod: the default roster on a machine that only has Codex signed in.
const agents = [
  makeAgent("nova", "FullStackDev", "claude-sonnet-4-6"),
  makeAgent("luna", "PM", "claude-haiku-4-5"),
  makeAgent("scout", "QA", "claude-sonnet-4-6"),
];

const provider = (name: string, ready: boolean) => ({
  provider: name,
  cliFound: ready,
  authenticated: ready,
  models: null,
  detail: null,
});

const runner = {
  online: true,
  machineName: "laptop",
  daemonVersion: "1.0.0",
  connectedAt: null,
  lastSeenAt: null,
  providers: [provider("codex", true), provider("claude", false)],
  lastKnownProviders: [provider("codex", true), provider("claude", false)],
  lastKnownAt: "2026-09-07T10:00:00Z",
  lastKnownMachineName: "laptop",
  hasActiveTokens: true,
};

function renderBanner(props: Partial<Parameters<typeof ProviderMismatchBanner>[0]> = {}) {
  return render(
    <ToastProvider>
      <ProviderMismatchBanner agents={agents} projectId="proj-1" {...props} />
    </ToastProvider>
  );
}

/**
 * A mismatched agent's tasks don't fail, they sit waiting for a sign-in on the runner — which
 * is exactly what the banner has to say.
 */
describe("ProviderMismatchBanner (hosted)", () => {
  beforeEach(() => {
    mockApi.runner.status.mockResolvedValue(runner);
    mockApi.agents.switchProvider.mockResolvedValue({ switchedCount: 2, skippedQaCount: 1 });
  });

  it("warns that mismatched agents will wait and offers the ready provider", async () => {
    renderBanner();

    expect(await screen.findByText("hostedWarning")).toBeInTheDocument();
    expect(screen.getByText("switchTeam")).toBeInTheDocument();
  });

  it("switches the team to the ready provider through the hosted API", async () => {
    renderBanner();

    fireEvent.click(await screen.findByText("switchTeam"));

    await vi.waitFor(() => {
      expect(mockApi.agents.switchProvider).toHaveBeenCalledWith("proj-1", "OpenAI", "");
    });
  });

  it("stays silent when no provider is ready on the runner", async () => {
    mockApi.runner.status.mockResolvedValue({
      ...runner,
      providers: [provider("codex", false)],
      lastKnownProviders: [provider("codex", false)],
    });

    const { container } = renderBanner();

    await vi.waitFor(() => expect(mockApi.runner.status).toHaveBeenCalled());
    expect(container.textContent).not.toContain("hostedWarning");
  });

  it("stays silent when every non-QA agent is already on a ready provider", async () => {
    const { container } = renderBanner({
      agents: [makeAgent("nova", "FullStackDev", "gpt-5.5"), agents[2]],
    });

    await vi.waitFor(() => expect(mockApi.runner.status).toHaveBeenCalled());
    expect(container.textContent).not.toContain("hostedWarning");
  });

  it("uses the last-known coverage while the runner is offline", async () => {
    mockApi.runner.status.mockResolvedValue({ ...runner, online: false, providers: null });

    renderBanner();

    expect(await screen.findByText("hostedWarning")).toBeInTheDocument();
  });

  it("renders the compact form as a sentence plus a link to the team page", async () => {
    renderBanner({ compact: true });

    const link = await screen.findByText("reviewTeam");
    expect(link.closest("a")).toHaveAttribute("href", "/p/proj-1/team");
    expect(screen.queryByText("switchTeam")).not.toBeInTheDocument();
  });
});
