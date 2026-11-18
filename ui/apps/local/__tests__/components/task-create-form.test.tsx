import { describe, it, expect, beforeEach, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import "../mocks/signalr";
import { mockApi } from "../mocks/api";

vi.mock("@/lib/actions", () => ({
  createTaskAndReturn: vi.fn(),
  submitTask: vi.fn(),
  addTaskDependency: vi.fn(),
}));

import { TaskCreateForm } from "@/components/task/TaskCreateForm";
import { ToastProvider } from "@/components/ui";
import type { ProjectAgent, ProjectRepo } from "@/lib/types";
import type { TaskBillingContext, TaskModelOption } from "@/components/task/TaskCreateForm";
import { annotateModels, runnerCoverage } from "@/lib/model-billing";
import type { AiModel } from "@/lib/types";

const repos = [{ id: "r1", name: "app-repo" }] as ProjectRepo[];

function makeAgent(id: string, name: string, model: string): ProjectAgent {
  return {
    id,
    projectId: "proj-1",
    repoIds: [],
    name,
    role: "FullStackDev",
    avatar: name.toLowerCase(),
    model,
    systemPrompt: "persona",
    userInstructions: "",
    isDefault: true,
    // The stats half of the record. Filled in rather than cast away: the cast no longer
    // type-checks, and an under-built fixture is how a component quietly starts depending
    // on a field the test never provides.
    totalTokensIn: 0,
    totalTokensOut: 0,
    billed: null,
    totalCostUsd: 0,
    tasksCompleted: 0,
    tasksFailed: 0,
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
  };
}

const agents = [
  makeAgent("a1", "Nova", "claude-sonnet-4-6"),
  makeAgent("a2", "Pixel", "claude-haiku-4-5"),
];

// The catalog the page passes down — mixed providers so the <optgroup> grouping is exercised.
const models: TaskModelOption[] = [
  { id: "claude-haiku-4-5", name: "Claude Haiku 4.5", provider: "Anthropic", sortOrder: 10 },
  { id: "claude-sonnet-4-6", name: "Claude Sonnet 4.6", provider: "Anthropic", sortOrder: 20 },
  { id: "claude-opus-5", name: "Claude Opus 5", provider: "Anthropic", sortOrder: 30 },
  { id: "gemini-3-pro-preview", name: "Gemini 3 Pro", provider: "Google", sortOrder: 40 },
];

function renderForm(
  modelOptions: TaskModelOption[] | undefined = models,
  billingContext?: TaskBillingContext
) {
  const result = render(
    <ToastProvider>
      <TaskCreateForm
        projectId="proj-1"
        repos={repos}
        agents={agents}
        models={modelOptions}
        billingContext={billingContext}
      />
    </ToastProvider>
  );
  const selects = [
    ...result.container.querySelectorAll<HTMLSelectElement>("select"),
  ];
  return {
    ...result,
    agentSelect: selects.find((s) => s.name === "assignedAgentId")!,
    // The model picker is the only unnamed select — its value rides a hidden input
    // so an untouched choice submits nothing.
    modelSelect: selects.find((s) => !s.name)!,
    modelInput: result.container.querySelector<HTMLInputElement>(
      'input[name="model"]'
    )!,
    form: result.container.querySelector<HTMLFormElement>("form")!,
    skipPlanningInput: result.container.querySelector<HTMLInputElement>(
      'input[name="skipPlanning"]'
    )!,
  };
}

describe("TaskCreateForm model picker", () => {
  it("starts on the agent default and submits no override", () => {
    const { modelSelect, modelInput } = renderForm();

    expect(modelSelect.value).toBe("");
    expect(modelInput.value).toBe("");
  });

  it("snaps to the selected agent's own model without sending an override", () => {
    const { agentSelect, modelSelect, modelInput } = renderForm();

    fireEvent.change(agentSelect, { target: { value: "a2" } });

    // Pre-selected from the Team page, so there is nothing to pin — the task keeps
    // tracking whatever the agent is configured with at run time.
    expect(modelSelect.value).toBe("claude-haiku-4-5");
    expect(modelInput.value).toBe("");
  });

  it("sends the override once the user picks a different model", () => {
    const { agentSelect, modelSelect, modelInput } = renderForm();

    fireEvent.change(agentSelect, { target: { value: "a1" } });
    fireEvent.change(modelSelect, { target: { value: "claude-opus-5" } });

    expect(modelInput.value).toBe("claude-opus-5");
  });

  it("sends the override when no agent is pinned", () => {
    const { modelSelect, modelInput } = renderForm();

    fireEvent.change(modelSelect, { target: { value: "claude-opus-5" } });

    expect(modelInput.value).toBe("claude-opus-5");
  });

  it("clears the override when the agent changes back to one matching the model", () => {
    const { agentSelect, modelSelect, modelInput } = renderForm();

    fireEvent.change(agentSelect, { target: { value: "a1" } });
    fireEvent.change(modelSelect, { target: { value: "claude-haiku-4-5" } });
    expect(modelInput.value).toBe("claude-haiku-4-5");

    // Pixel already runs Haiku, so the pin is redundant again.
    fireEvent.change(agentSelect, { target: { value: "a2" } });
    expect(modelSelect.value).toBe("claude-haiku-4-5");
    expect(modelInput.value).toBe("");
  });
});

describe("TaskCreateForm model catalog", () => {
  it("groups the options by provider", () => {
    const { modelSelect } = renderForm();

    expect(
      [...modelSelect.querySelectorAll("optgroup")].map((g) => g.getAttribute("label"))
    ).toEqual(["Claude", "Google"]);
  });

  it("offers only the agent default when the catalog is unavailable", () => {
    // Still a fully working form: no pin means the task tracks the agent's own model.
    const { modelSelect, modelInput } = renderForm([]);

    expect([...modelSelect.options].map((o) => o.value)).toEqual([""]);
    expect(modelInput.value).toBe("");
  });

  it("keeps an agent's model listed even when the catalog omits it", () => {
    const { agentSelect, modelSelect } = renderForm([
      { id: "claude-opus-5", name: "Claude Opus 5", provider: "Anthropic", sortOrder: 10 },
    ]);

    // Nova runs claude-sonnet-4-6, which this catalog doesn't list.
    fireEvent.change(agentSelect, { target: { value: "a1" } });

    expect(modelSelect.value).toBe("claude-sonnet-4-6");
    expect([...modelSelect.options].map((o) => o.value)).toContain("claude-sonnet-4-6");
  });
});

describe("TaskCreateForm skip planning", () => {
  it("defaults to off and submits nothing", () => {
    const { form, skipPlanningInput } = renderForm();

    expect(skipPlanningInput.checked).toBe(false);
    // Unchecked checkboxes are absent from the FormData, which the parser reads as false.
    expect(new FormData(form).get("skipPlanning")).toBeNull();
  });

  it('submits "true" once checked', () => {
    const { form, skipPlanningInput } = renderForm();

    fireEvent.click(skipPlanningInput);

    expect(skipPlanningInput.checked).toBe(true);
    expect(new FormData(form).get("skipPlanning")).toBe("true");
  });
});

// ── Hosted billing note ─────────────────────────────────────────────────

function catalogModel(overrides: Partial<AiModel> & { id: string; provider: string }): AiModel {
  return {
    name: overrides.id,
    kind: "Chat",
    description: "",
    supportsMcp: true,
    isEnabled: true,
    requiresRunner: false,
    sortOrder: 0,
    ...overrides,
  };
}

const catalog: AiModel[] = [
  catalogModel({ id: "claude-haiku-4-5", name: "Claude Haiku 4.5", provider: "Anthropic", sortOrder: 10 }),
  catalogModel({ id: "claude-sonnet-4-6", name: "Claude Sonnet 4.6", provider: "Anthropic", sortOrder: 20 }),
  catalogModel({ id: "gpt-5.5", name: "GPT-5.5", provider: "OpenAI", sortOrder: 30 }),
  catalogModel({ id: "copilot/auto", name: "Copilot Auto", provider: "GitHubCopilot", requiresRunner: true, sortOrder: 40 }),
];

const ready = (provider: string, isReady: boolean) => ({
  provider,
  cliFound: isReady,
  authenticated: isReady,
  models: null,
});

/** A runner with Claude signed in and nothing else. */
function claudeOnlyContext(): { context: TaskBillingContext; options: TaskModelOption[] } {
  const coverage = runnerCoverage({
    online: true,
    providers: [ready("claude", true), ready("codex", false), ready("copilot", false)],
  });
  const annotated = annotateModels(catalog, coverage);
  return {
    context: { coverage, annotated },
    options: annotated.map((m) => ({
      id: m.id,
      name: m.name,
      provider: m.provider,
      sortOrder: m.sortOrder,
      billing: m.billing,
      reason: m.reason,
    })),
  };
}

describe("TaskCreateForm runner readiness note", () => {
  beforeEach(() => {
    mockApi.tasks.forecastDraft.mockResolvedValue(null);
  });

  it("labels options with runner readiness and disables the ones nobody can run", () => {
    const { options, context } = claudeOnlyContext();
    // A runner-only model on a runner without it is hidden by annotateModels; pass one
    // through explicitly to pin the form's own rendering rule.
    const { modelSelect } = renderForm(
      [...options, { id: "ollama/qwen3", name: "Qwen3", provider: "Ollama", billing: "unavailable", reason: "runnerOnlyNotReady" }],
      context
    );

    const byId = (id: string) => [...modelSelect.options].find((o) => o.value === id);
    expect(byId("claude-sonnet-4-6")?.textContent).toBe("modelBilling.optionRunner");
    expect(byId("claude-sonnet-4-6")?.disabled).toBe(false);
    // Not signed in on the runner: still pickable — the task waits for the sign-in.
    expect(byId("gpt-5.5")?.textContent).toBe("taskCreateForm.modelNotConnected");
    expect(byId("gpt-5.5")?.disabled).toBe(false);
    expect(byId("ollama/qwen3")?.textContent).toBe("taskCreateForm.modelNotConnected");
    expect(byId("ollama/qwen3")?.disabled).toBe(true);
    // Hidden entirely: the runner can't run it.
    expect(byId("copilot/auto")).toBeUndefined();
  });

  it("keeps Local's plain labels when no billing context is given", () => {
    const { modelSelect, container } = renderForm(models);
    expect([...modelSelect.options].map((o) => o.textContent)).toContain("Claude Opus 5");
    expect(container.querySelector("[data-billing]")).toBeNull();
  });

  it("shows the green runner note for a model the runner covers", async () => {
    const { options, context } = claudeOnlyContext();
    const { modelSelect, container } = renderForm(options, context);

    fireEvent.change(modelSelect, { target: { value: "claude-sonnet-4-6" } });

    await waitFor(() => {
      expect(container.querySelector('[data-billing="runner"]')?.textContent).toContain(
        "runner"
      );
    });
  });

  it("resolves the agent default to the pinned agent's model, else the default dev agent", async () => {
    const { options, context } = claudeOnlyContext();
    const { agentSelect, modelSelect, container } = renderForm(options, context);

    // Nothing picked: Nova (FullStackDev, cross-repo) is the assigner's choice → Sonnet → runner.
    await waitFor(() => {
      expect(container.querySelector('[data-billing="runner"]')).not.toBeNull();
    });

    // Pin Pixel and clear the model: Pixel's own Haiku decides — still Claude, still free.
    fireEvent.change(agentSelect, { target: { value: "a2" } });
    fireEvent.change(modelSelect, { target: { value: "" } });
    await waitFor(() => {
      expect(container.querySelector('[data-billing="runner"]')).not.toBeNull();
    });

    // An explicit pick outranks both: Codex isn't signed in, so the task would wait for it.
    fireEvent.change(modelSelect, { target: { value: "gpt-5.5" } });
    await waitFor(() => {
      expect(
        container.querySelector('[data-billing="runner-waiting"]')?.textContent
      ).toContain("runnerWaitingSignIn");
    });
    const link = container.querySelector('[data-billing="runner-waiting"] a');
    expect(link).toHaveAttribute("href", "/settings/machine");
    expect(link?.textContent).toBe("settingsLink");
  });

  it("sends the pinned agent along with the draft forecast", async () => {
    const { options, context } = claudeOnlyContext();
    const { agentSelect } = renderForm(options, context);

    fireEvent.change(agentSelect, { target: { value: "a2" } });

    await waitFor(() => {
      expect(mockApi.tasks.forecastDraft).toHaveBeenLastCalledWith(
        "proj-1",
        { model: "claude-haiku-4-5", assignedAgentId: "a2" },
        ""
      );
    });
  });
});

describe("TaskCreateForm attachments", () => {
  it("lists a file chosen through the picker, even though the input is reset right after", async () => {
    const { container, agentSelect } = renderForm();
    const input = container.querySelector<HTMLInputElement>('input[type="file"]')!;
    const file = new File(["hello"], "spec.pdf", { type: "application/pdf" });

    // A browser FileList is live: resetting the input's value empties it. The change handler
    // resets the input right after handing the list over (so the same file can be picked
    // twice), which is exactly what made a lazily-read list come up empty.
    const backing: File[] = [file];
    const liveList = {
      get length() {
        return backing.length;
      },
      item: (i: number) => backing[i] ?? null,
      *[Symbol.iterator]() {
        yield* backing;
      },
    } as unknown as FileList;
    Object.defineProperty(input, "files", { configurable: true, get: () => liveList });
    Object.defineProperty(input, "value", {
      configurable: true,
      get: () => "",
      set: (v: string) => {
        if (v === "") backing.length = 0;
      },
    });

    // In the real page the form always has some other update in flight (the estimate,
    // the description editor), so React defers the files updater
    // instead of computing it eagerly. Model that: a state-backed pick pending in the
    // same batch.
    await act(async () => {
      fireEvent.change(agentSelect, { target: { value: "a2" } });
      fireEvent.change(input);
    });

    expect(await screen.findByText("spec.pdf")).toBeInTheDocument();
  });
});

