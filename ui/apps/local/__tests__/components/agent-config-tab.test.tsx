import { describe, it, expect, beforeEach, vi } from "vitest";
import { fireEvent, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";
import { renderWithPermissions } from "../render-with-permissions";

vi.mock("@/lib/actions", () => ({
  updateAgent: vi.fn(),
  deleteAgent: vi.fn(),
}));

import { AgentConfigTab } from "@/app/[locale]/(dashboard)/p/[projectId]/team/AgentConfigTab";
import type { AiModel, ProjectAgent, ProjectRepo } from "@/lib/types";

const repos = [
  { id: "r1", name: "game-repo" },
  { id: "r2", name: "web-repo" },
] as ProjectRepo[];

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

// A mixed-provider catalog, as GET /api/models returns it.
const models: AiModel[] = [
  model({ id: "claude-sonnet-4-6", name: "Claude Sonnet 4.6", sortOrder: 10 }),
  model({ id: "claude-opus-5", name: "Claude Opus 5", sortOrder: 20 }),
  model({ id: "gemini-3-pro-preview", name: "Gemini 3 Pro", provider: "Google",
          supportsMcp: false, sortOrder: 30 }),
  model({ id: "gpt-5.5", name: "GPT-5.5", provider: "OpenAI",
          supportsMcp: false, sortOrder: 40 }),
];

function makeAgent(overrides: Partial<ProjectAgent>): ProjectAgent {
  return {
    id: "a1",
    projectId: "proj-1",
    repoIds: [],
    name: "Rune",
    role: "GameDev",
    avatar: "rune",
    model: "claude-sonnet-4-6",
    systemPrompt: "persona",
    userInstructions: "",
    isDefault: false,
    totalTokensIn: 0,
    totalTokensOut: 0,
    totalCostUsd: 0,
    tasksCompleted: 0,
    tasksFailed: 0,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  } as ProjectAgent;
}

describe("AgentConfigTab", () => {
  it("shows a repo checkbox per repository for dev roles, pre-checking the scope", () => {
    const { container, getByText } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({ repoIds: ["r1"] })}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    getByText("agentConfig.repositoriesLabel");
    const boxes = container.querySelectorAll<HTMLInputElement>('input[name="repoIds"]');
    expect(boxes).toHaveLength(2);
    expect(boxes[0].value).toBe("r1");
    expect(boxes[0].checked).toBe(true);
    expect(boxes[1].checked).toBe(false);
    // Marker distinguishes "none checked" from "group not rendered" server-side.
    expect(container.querySelector('input[name="repoIdsPresent"]')).not.toBeNull();
  });

  it("hides the repository checkboxes for non-dev roles", () => {
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({ name: "Sage", role: "DBA" })}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    expect(container.querySelector('input[name="repoIds"]')).toBeNull();
    expect(container.querySelector('input[name="repoIdsPresent"]')).toBeNull();
  });

  it("shows the default-agent notice instead of a delete button for seeded agents", () => {
    const { container, queryByText } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({ isDefault: true })}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    expect(container.textContent).toContain("agentConfig.defaultAgent");
    expect(queryByText("agentConfig.deleteAgent")).toBeNull();
  });

  // ── Model catalog ─────────────────────────────────────────────────────

  it("renders the catalog grouped by provider", () => {
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({})}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    const groups = container.querySelectorAll("select[name='model'] optgroup");
    expect([...groups].map((g) => g.getAttribute("label"))).toEqual([
      "Claude",
      "Google",
      "OpenAI",
    ]);
    expect(container.querySelectorAll("select[name='model'] option")).toHaveLength(4);
  });

  it("disables non-MCP models for the QA role", () => {
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({ name: "Scout", role: "QA" })}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    const options = [...container.querySelectorAll<HTMLOptionElement>(
      "select[name='model'] option"
    )];
    const disabled = options.filter((o) => o.disabled).map((o) => o.value);
    expect(disabled).toEqual(["gemini-3-pro-preview", "gpt-5.5"]);
  });

  it("keeps a model the catalog no longer offers listed and selected", () => {
    // Otherwise the browser selects options[0] and saving silently moves the agent onto a
    // different model.
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({ model: "claude-opus-4-1" })}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    const select = container.querySelector<HTMLSelectElement>("select[name='model']")!;
    expect(select.value).toBe("claude-opus-4-1");
    const stale = [...select.options].find((o) => o.value === "claude-opus-4-1")!;
    expect(stale.textContent).toContain("agentConfig.noLongerAvailable");
    expect(stale.disabled).toBe(false);
  });

  it("omits the model select entirely when the catalog is unavailable", () => {
    // No select ⇒ no "model" key in the FormData ⇒ the server action leaves it unchanged.
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({})}
        repos={repos}
        models={[]}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    expect(container.querySelector("select[name='model']")).toBeNull();
    expect(container.textContent).toContain("agentConfig.catalogUnavailable");
  });

  it("offers no image-model picker to design agents", () => {
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({ name: "Muse", role: "Designer" })}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    expect(container.querySelector("select[name='imageProvider']")).toBeNull();
  });
});

// ── Runner-aware billing labels ────────────────────────────────────────

const readyRow = (provider: string, isReady: boolean) => ({
  provider,
  cliFound: isReady,
  authenticated: isReady,
  models: null,
  detail: null,
});

const claudeOnlyRunner = {
  online: true,
  machineName: "laptop",
  daemonVersion: "1.0.0",
  connectedAt: null,
  lastSeenAt: null,
  providers: [readyRow("claude", true), readyRow("codex", false), readyRow("copilot", false)],
  lastKnownProviders: null,
  lastKnownAt: null,
  lastKnownMachineName: null,
  hasActiveTokens: true,
};

describe("AgentConfigTab runner readiness labels", () => {
  beforeEach(() => {
    mockApi.runner.status.mockResolvedValue(claudeOnlyRunner);
  });

  it("labels each option with runner readiness and notes the saved model under the select", async () => {
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({})}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );

    await waitFor(() => {
      expect(container.querySelector('[data-billing="runner"]')).not.toBeNull();
    });
    const byId = (id: string) =>
      [...container.querySelectorAll<HTMLOptionElement>("select[name='model'] option")].find(
        (o) => o.value === id
      );
    expect(byId("claude-sonnet-4-6")?.textContent).toBe("modelBilling.optionRunner");
    expect(byId("gpt-5.5")?.textContent).toBe("taskCreateForm.modelNotConnected");
    expect(byId("gpt-5.5")?.disabled).toBe(false);
  });

  it("switches the note to waiting for a sign-in when a non-covered model is picked", async () => {
    const { container } = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({})}
        repos={repos}
        models={models}
        projectId="proj-1"
        onClose={() => {}}
      />
    );
    await waitFor(() => {
      expect(container.querySelector('[data-billing="runner"]')).not.toBeNull();
    });

    fireEvent.change(container.querySelector("select[name='model']")!, {
      target: { value: "gpt-5.5" },
    });

    expect(container.querySelector('[data-billing="runner-waiting"]')?.textContent).toContain(
      "runnerWaitingSignIn"
    );
    expect(container.querySelector('[data-billing="runner-waiting"] a')).toHaveAttribute(
      "href",
      "/settings/machine"
    );
  });

  it("hides runner-only models the runner lacks but keeps the agent's saved one, enabled", async () => {
    const withCopilot = [
      ...models,
      model({ id: "copilot/auto", name: "Copilot Auto", provider: "GitHubCopilot",
              requiresRunner: true, supportsMcp: false, sortOrder: 50 }),
    ];

    const hidden = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({})}
        repos={repos}
        models={withCopilot}
        projectId="proj-1"
        onClose={() => {}}
      />
    );
    await waitFor(() => {
      expect(hidden.container.querySelector('[data-billing="runner"]')).not.toBeNull();
    });
    expect(
      [...hidden.container.querySelectorAll<HTMLOptionElement>("select[name='model'] option")]
        .map((o) => o.value)
    ).not.toContain("copilot/auto");
    hidden.unmount();

    const saved = renderWithPermissions(
      <AgentConfigTab
        agent={makeAgent({ model: "copilot/auto" })}
        repos={repos}
        models={withCopilot}
        projectId="proj-1"
        onClose={() => {}}
      />
    );
    await waitFor(() => {
      expect(saved.container.querySelector('[data-billing="unavailable"]')).not.toBeNull();
    });
    const select = saved.container.querySelector<HTMLSelectElement>("select[name='model']")!;
    const row = [...select.options].find((o) => o.value === "copilot/auto")!;
    expect(row.textContent).toBe("taskCreateForm.modelNotConnected");
    expect(row.disabled).toBe(false);
    expect(select.value).toBe("copilot/auto");
  });
});
