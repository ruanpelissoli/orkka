import { describe, it, expect, vi } from "vitest";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { renderWithPermissions } from "../render-with-permissions";

// The detail panel drags in chat/SignalR — out of scope for grid rendering.
vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/team/AgentDetailPanel",
  () => ({ AgentDetailPanel: () => null })
);

// Covered by its own test (team-model-control); it needs a ToastProvider and the runner status.
vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/team/TeamModelControl",
  () => ({ TeamModelControl: () => null })
);

// Covered by workspace-ai-provider.test; it reads the runner status and the workspace setting.
vi.mock(
  "@/app/[locale]/(dashboard)/p/[projectId]/team/TeamProviderNotes",
  () => ({ TeamProviderNotes: () => null })
);

import { AgentGrid } from "@/app/[locale]/(dashboard)/p/[projectId]/team/AgentGrid";
import type { AiModel, ProjectAgent } from "@/lib/types";

function makeAgent(overrides: Partial<ProjectAgent>): ProjectAgent {
  return {
    id: "a1",
    projectId: "proj-1",
    repoIds: [],
    name: "Luna",
    role: "PM",
    avatar: "luna",
    model: "claude-sonnet-4-6",
    systemPrompt: "",
    userInstructions: "",
    isDefault: true,
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

const models: AiModel[] = [
  {
    id: "claude-sonnet-4-6",
    name: "Claude Sonnet 4.6",
    provider: "Anthropic",
    kind: "Chat",
    description: "",
    supportsMcp: true,
    isEnabled: true,
    requiresRunner: false,
    sortOrder: 10,
  },
];

describe("AgentGrid", () => {
  it("renders a card for every seeded agent", () => {
    const agents = [
      makeAgent({}),
      makeAgent({ id: "a2", name: "Sage", role: "DBA", avatar: "sage" }),
      makeAgent({ id: "a3", name: "Lex", role: "Legal", avatar: "lex" }),
    ];

    const { container } = renderWithPermissions(
      <AgentGrid agents={agents} repos={[]} models={models} projectId="proj-1" />
    );

    expect(container.textContent).toContain("Luna");
    expect(container.textContent).toContain("Sage");
    expect(container.textContent).toContain("Lex");
    expect(container.textContent).toContain("Legal");
  });

  it("has no Add Agent button or Enable ghost cards — the roster is fixed", () => {
    const { queryByText } = renderWithPermissions(
      <AgentGrid agents={[makeAgent({})]} repos={[]} models={models} projectId="proj-1" />
    );

    expect(queryByText("Add Agent")).toBeNull();
    expect(queryByText("Enable")).toBeNull();
  });

  it("labels the model from the catalog, falling back to the raw id", () => {
    const { container } = renderWithPermissions(
      <AgentGrid
        agents={[makeAgent({}), makeAgent({ id: "a2", name: "Nova", model: "ollama/qwen3-coder:30b" })]}
        repos={[]}
        models={models}
       
        projectId="proj-1"
      />
    );

    expect(container.textContent).toContain("Claude Sonnet 4.6");
    // Not in the catalog (a locally-pulled Ollama tag) — the raw id is still shown.
    expect(container.textContent).toContain("ollama/qwen3-coder:30b");
  });
});
