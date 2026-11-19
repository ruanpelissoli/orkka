import { describe, it, expect, beforeEach, vi } from "vitest";
import { fireEvent, render, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";

vi.mock("@/lib/actions", () => ({
  setWorkspaceAiProvider: vi.fn(),
}));

import { WorkspaceAiProviderCard } from "@/components/settings/WorkspaceAiProviderCard";
import { TeamProviderNotes } from "@/app/[locale]/(dashboard)/p/[projectId]/team/TeamProviderNotes";
import { ToastProvider } from "@/components/ui";
import { setWorkspaceAiProvider } from "@/lib/actions";
import type { ProjectAgent } from "@/lib/types";

const row = (provider: string, ready: boolean, installed = true) => ({
  provider,
  cliFound: installed,
  authenticated: ready,
  models: null,
  detail: null,
  authMode: null,
  authDetail: null,
});

const runner = (providers: ReturnType<typeof row>[]) => ({
  online: true,
  machineName: "laptop",
  daemonVersion: "1.0.0",
  connectedAt: null,
  lastSeenAt: null,
  providers,
  lastKnownProviders: null,
  lastKnownAt: null,
  lastKnownMachineName: null,
  hasActiveTokens: true,
});

const setting = (provider: string, overrides: Record<string, unknown> = {}) => ({
  provider,
  choices: ["automatic", "claude", "codex", "copilot", "gemini", "grok"],
  effectiveProvider: provider === "automatic" ? "claude" : provider,
  blocker: null,
  ...overrides,
});

function renderCard() {
  return render(
    <ToastProvider>
      <WorkspaceAiProviderCard />
    </ToastProvider>
  );
}

describe("WorkspaceAiProviderCard", () => {
  beforeEach(() => {
    vi.mocked(setWorkspaceAiProvider).mockReset();
    mockApi.org.aiProvider.mockResolvedValue(setting("automatic"));
    mockApi.runner.status.mockResolvedValue(
      runner([row("claude", true), row("codex", false), row("copilot", true), row("gemini", false, false)])
    );
  });

  it("lists every choice with this machine's readiness and the command that fixes it", async () => {
    const { findByTestId, getByTestId } = renderCard();

    const copilot = await findByTestId("ai-provider-copilot");
    expect(copilot.textContent).toContain("readiness.ready");
    expect(getByTestId("ai-provider-automatic").querySelector("input")!.checked).toBe(true);

    // Codex is installed but signed out: its own sign-in command.
    const codex = getByTestId("ai-provider-codex");
    expect(codex.textContent).toContain("readiness.notSignedIn");
    expect(codex.querySelector("code")!.textContent).toBe("codex login");

    // Gemini is not installed: the CLI itself.
    const gemini = getByTestId("ai-provider-gemini");
    expect(gemini.textContent).toContain("readiness.notInstalled");
    expect(gemini.querySelector("code")!.textContent).toBe("gemini");

    // A ready provider shows no fix.
    expect(copilot.querySelector("code")).toBeNull();
  });

  it("asks before saving, switches existing projects by default, and reports the counts", async () => {
    vi.mocked(setWorkspaceAiProvider).mockResolvedValue({
      provider: "copilot",
      effectiveProvider: "copilot",
      blocker: null,
      applied: true,
      projectCount: 2,
      updatedCount: 24,
      unchangedCount: 0,
      skippedQaCount: 2,
    });
    const { findByTestId, getByText, findByText, queryByText } = renderCard();

    fireEvent.click((await findByTestId("ai-provider-copilot")).querySelector("input")!);
    // QA stays on Claude whatever is chosen — said before saving.
    getByText("qaNote");
    fireEvent.click(getByText("save"));

    expect(setWorkspaceAiProvider).not.toHaveBeenCalled();
    await findByText("confirmTitle");
    const apply = getByText("applyExisting").closest("label")!.querySelector("input")!;
    expect(apply.checked).toBe(true);

    fireEvent.click(getByText("confirm"));

    await waitFor(() => expect(setWorkspaceAiProvider).toHaveBeenCalledWith("copilot", true));
    await findByText("savedApplied savedQa");
    await waitFor(() => expect(queryByText("confirmTitle")).toBeNull());
  });

  it("can change only new projects", async () => {
    vi.mocked(setWorkspaceAiProvider).mockResolvedValue({
      provider: "copilot",
      effectiveProvider: "copilot",
      blocker: null,
      applied: false,
      projectCount: 0,
      updatedCount: 0,
      unchangedCount: 0,
      skippedQaCount: 0,
    });
    const { findByTestId, getByText, findByText } = renderCard();

    fireEvent.click((await findByTestId("ai-provider-copilot")).querySelector("input")!);
    fireEvent.click(getByText("save"));
    await findByText("confirmTitle");
    fireEvent.click(getByText("applyExisting").closest("label")!.querySelector("input")!);
    fireEvent.click(getByText("confirm"));

    await waitFor(() => expect(setWorkspaceAiProvider).toHaveBeenCalledWith("copilot", false));
    await findByText("saved");
  });

  it("says why a chosen provider is not what new projects get", async () => {
    mockApi.org.aiProvider.mockResolvedValue(
      setting("copilot", { effectiveProvider: "claude", blocker: "copilot is not logged in." })
    );
    const { findByTestId } = renderCard();

    expect((await findByTestId("ai-provider-blocker")).textContent).toBe("blocked");
  });

  it("keeps Save disabled until the choice changes", async () => {
    const { findByTestId, getByText } = renderCard();
    await findByTestId("ai-provider-automatic");

    expect(getByText("save").closest("button")!.disabled).toBe(true);
  });
});

describe("TeamProviderNotes", () => {
  const agents = [
    { id: "a1", role: "FullStackDev", name: "Nova", model: "copilot/auto" },
    { id: "a2", role: "QA", name: "Scout", model: "claude-sonnet-5" },
  ] as ProjectAgent[];

  beforeEach(() => {
    mockApi.org.aiProvider.mockResolvedValue(setting("automatic"));
  });

  it("says browser QA needs Claude when Claude is not signed in", async () => {
    mockApi.runner.status.mockResolvedValue(runner([row("claude", false), row("copilot", true)]));
    const { findByTestId, queryByTestId } = render(<TeamProviderNotes agents={agents} />);

    expect((await findByTestId("qa-needs-claude")).textContent).toContain("qaNeedsClaude");
    expect(queryByTestId("choose-provider-hint")).toBeNull();
  });

  it("hints that the provider can be chosen when Copilot and Claude are both signed in on Automatic", async () => {
    mockApi.runner.status.mockResolvedValue(runner([row("claude", true), row("copilot", true)]));
    const { findByTestId, queryByTestId } = render(<TeamProviderNotes agents={agents} />);

    expect((await findByTestId("choose-provider-hint")).textContent).toContain("chooseProvider");
    expect(queryByTestId("qa-needs-claude")).toBeNull();
  });

  it("stays silent once a provider is chosen, or with only Copilot signed in", async () => {
    mockApi.org.aiProvider.mockResolvedValue(setting("copilot"));
    mockApi.runner.status.mockResolvedValue(runner([row("claude", true), row("copilot", true)]));
    const chosen = render(<TeamProviderNotes agents={agents.filter((a) => a.role !== "QA")} />);
    await waitFor(() => expect(mockApi.org.aiProvider).toHaveBeenCalled());
    await waitFor(() => expect(mockApi.runner.status).toHaveBeenCalled());
    expect(chosen.queryByTestId("team-provider-notes")).toBeNull();
    chosen.unmount();

    mockApi.org.aiProvider.mockResolvedValue(setting("automatic"));
    mockApi.runner.status.mockResolvedValue(runner([row("claude", true), row("copilot", false)]));
    const automatic = render(<TeamProviderNotes agents={agents} />);
    await waitFor(() => expect(mockApi.runner.status).toHaveBeenCalled());
    expect(automatic.queryByTestId("team-provider-notes")).toBeNull();
  });
});
