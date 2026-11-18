import { describe, it, expect } from "vitest";
import {
  RUNNER_PROVIDER_PREFERENCE,
  annotateModels,
  billingForModel,
  cliProviderLabel,
  isProviderReady,
  ollamaModelName,
  openCodeModelName,
  readyProviders,
  runnerCoverage,
  type AnnotatedModel,
  type RunnerCoverage,
} from "@/lib/model-billing";
import type { AiModel, RunnerProviderStatus } from "@/lib/types";

function model(overrides: Partial<AiModel> & Pick<AiModel, "id" | "provider">): AiModel {
  return {
    name: overrides.id,
    kind: "Chat",
    description: "",
    supportsMcp: false,
    isEnabled: true,
    requiresRunner: false,
    sortOrder: 0,
    ...overrides,
  };
}

function status(
  provider: string,
  overrides: Partial<RunnerProviderStatus> = {}
): RunnerProviderStatus {
  return {
    provider,
    cliFound: true,
    authenticated: true,
    models: null,
    detail: null,
    ...overrides,
  };
}

const OPUS = model({ id: "claude-opus-5", provider: "Anthropic" });
const GPT = model({ id: "gpt-5.6-sol", provider: "OpenAI" });
const COPILOT = model({ id: "copilot/auto", provider: "GitHubCopilot", requiresRunner: true });
const OLLAMA_ANCHOR = model({
  id: "ollama/gpt-oss:20b",
  provider: "Ollama",
  name: "GPT-OSS 20B (catalog)",
  requiresRunner: true,
});
const OPENCODE_ANCHOR = model({
  id: "opencode/anthropic/claude-sonnet-4-6",
  provider: "OpenCode",
  requiresRunner: true,
});
const RETIRED = model({ id: "claude-sonnet-4", provider: "Anthropic", isEnabled: false });

const CATALOG = [OPUS, GPT, COPILOT, OLLAMA_ANCHOR, OPENCODE_ANCHOR, RETIRED];

const live = (...providers: RunnerProviderStatus[]): RunnerCoverage =>
  runnerCoverage({ online: true, providers })!;

const byId = (rows: AnnotatedModel[], id: string) => rows.find((m) => m.id === id);
const ids = (rows: AnnotatedModel[]) => rows.map((m) => m.id);

describe("readyProviders", () => {
  it("is ready only when the CLI is found AND authenticated", () => {
    expect(isProviderReady(status("claude"))).toBe(true);
    expect(isProviderReady(status("claude", { cliFound: false }))).toBe(false);
    expect(isProviderReady(status("claude", { authenticated: false }))).toBe(false);

    const ready = readyProviders([
      status("codex", { cliFound: false }), // credentials on disk, CLI missing
      status("gemini", { authenticated: false }), // CLI present, logged out
      status("claude"),
    ]);
    expect(ready.map((p) => p.provider)).toEqual(["claude"]);
  });

  it("returns ready providers in preference order, whatever order the runner reported", () => {
    const ready = readyProviders([
      status("ollama"),
      status("opencode"),
      status("copilot"),
      status("grok"),
      status("gemini"),
      status("codex"),
      status("claude"),
    ]);
    expect(ready.map((p) => p.provider)).toEqual([...RUNNER_PROVIDER_PREFERENCE]);
    expect(RUNNER_PROVIDER_PREFERENCE).toEqual([
      "claude",
      "codex",
      "gemini",
      "grok",
      "copilot",
      "opencode",
      "ollama",
    ]);
  });

  it("tolerates a missing provider list", () => {
    expect(readyProviders(null)).toEqual([]);
    expect(readyProviders(undefined)).toEqual([]);
  });

  it("labels CLI keys by product name and passes unknown keys through", () => {
    expect(cliProviderLabel("codex")).toBe("Codex");
    expect(cliProviderLabel("opencode")).toBe("OpenCode");
    expect(cliProviderLabel("mystery")).toBe("mystery");
  });
});

describe("runnerCoverage", () => {
  it("is null when nothing is known", () => {
    expect(runnerCoverage(null)).toBeNull();
    expect(runnerCoverage(undefined)).toBeNull();
    expect(runnerCoverage({ online: false, providers: null })).toBeNull();
    expect(
      runnerCoverage({ online: false, providers: null, lastKnownProviders: null })
    ).toBeNull();
  });

  it("uses the live providers when online (not stale, even if a snapshot exists)", () => {
    const c = runnerCoverage({
      online: true,
      providers: [status("claude"), status("codex", { authenticated: false })],
      lastKnownProviders: [status("codex")],
      lastKnownAt: "2026-09-06T10:00:00Z",
    })!;
    expect(c.online).toBe(true);
    expect(c.stale).toBe(false);
    expect(c.asOf).toBeNull();
    expect([...c.ready]).toEqual(["claude"]);
  });

  it("falls back to the last-known snapshot when offline and flags it stale", () => {
    const c = runnerCoverage({
      online: false,
      providers: null,
      lastKnownProviders: [status("codex"), status("claude", { cliFound: false })],
      lastKnownAt: "2026-09-06T10:00:00Z",
    })!;
    expect(c.online).toBe(false);
    expect(c.stale).toBe(true);
    expect(c.asOf).toBe("2026-09-06T10:00:00Z");
    expect([...c.ready]).toEqual(["codex"]);
  });

  it("collects discovered ids only from the discovering providers", () => {
    const c = live(
      status("ollama", { models: ["ollama/qwen3-coder:30b"] }),
      status("opencode", { models: ["opencode/openai/gpt-5"] }),
      status("claude", { models: ["claude-opus-5"] }) // not a discovering provider
    );
    expect(c.discovered).toEqual({
      ollama: ["ollama/qwen3-coder:30b"],
      opencode: ["opencode/openai/gpt-5"],
    });
  });
});

describe("annotateModels — rule table", () => {
  describe("runner coverage", () => {
    it("runs on the runner when the provider is ready live", () => {
      const rows = annotateModels(CATALOG, live(status("claude"), status("copilot")));
      expect(byId(rows, OPUS.id)).toMatchObject({
        billing: "runner",
        reason: "runnerReady",
        connected: true,
      });
      expect(byId(rows, COPILOT.id)).toMatchObject({
        billing: "runner",
        reason: "runnerReady",
        connected: true,
      });
    });

    it("runs on the runner from last-known coverage, flagged stale", () => {
      const coverage = runnerCoverage({
        online: false,
        providers: null,
        lastKnownProviders: [status("claude")],
        lastKnownAt: "2026-09-06T10:00:00Z",
      });
      const rows = annotateModels(CATALOG, coverage);
      expect(byId(rows, OPUS.id)).toMatchObject({
        billing: "runner",
        reason: "runnerReadyStale",
        connected: true,
      });
      // A provider the snapshot says is NOT signed in waits for a sign-in, not "offline".
      expect(byId(rows, GPT.id)).toMatchObject({
        billing: "runner",
        reason: "runnerWaitingSignIn",
        connected: false,
      });
    });

    it("keeps a catalog model on the runner, waiting, when its provider is not signed in", () => {
      const rows = annotateModels(
        CATALOG,
        live(status("claude"), status("codex", { authenticated: false }))
      );
      expect(byId(rows, GPT.id)).toMatchObject({
        billing: "runner",
        reason: "runnerWaitingSignIn",
        connected: false,
      });
    });

    it("treats credentials without a CLI on PATH as not signed in", () => {
      const rows = annotateModels(
        CATALOG,
        live(status("codex", { cliFound: false, authenticated: true }))
      );
      expect(byId(rows, GPT.id)).toMatchObject({ billing: "runner", reason: "runnerWaitingSignIn" });
    });

    it("routes to the runner (waiting) when no coverage is known at all", () => {
      const rows = annotateModels(CATALOG, null);
      expect(byId(rows, OPUS.id)).toMatchObject({
        billing: "runner",
        reason: "runnerOffline",
        connected: false,
      });
      // Nothing could run a runner-only model — hidden.
      expect(byId(rows, COPILOT.id)).toBeUndefined();
    });

    it("hides a runner-only model whose provider is not ready, unless it is the saved model", () => {
      const hidden = annotateModels(CATALOG, live(status("claude")));
      expect(byId(hidden, COPILOT.id)).toBeUndefined();

      const kept = annotateModels(CATALOG, live(status("claude")), {
        currentModel: COPILOT.id,
      });
      expect(byId(kept, COPILOT.id)).toMatchObject({
        billing: "unavailable",
        reason: "runnerOnlyNotReady",
        connected: false,
      });
    });
  });

  describe("catalog filtering", () => {
    it("drops image rows and retired models, keeping a retired saved model", () => {
      const rows = annotateModels(CATALOG, live(status("claude")));
      expect(ids(rows)).not.toContain(RETIRED.id);

      const kept = annotateModels(CATALOG, live(status("claude")), {
        currentModel: RETIRED.id,
      });
      expect(byId(kept, RETIRED.id)).toMatchObject({ billing: "runner", isEnabled: false });
    });

    it("matches the saved model case-insensitively", () => {
      const rows = annotateModels(CATALOG, live(status("claude")), {
        currentModel: "Copilot/Auto",
      });
      expect(byId(rows, COPILOT.id)).toBeDefined();
    });

    it("handles a missing catalog", () => {
      expect(annotateModels(null, null)).toEqual([]);
    });
  });
});

describe("annotateModels — discovered models (Ollama / OpenCode)", () => {
  it("keeps a saved catalog anchor while no coverage is known", () => {
    // Nothing can be replaced before the runner reports, so the anchor is still the row.
    const rows = annotateModels(CATALOG, null, { currentModel: OLLAMA_ANCHOR.id });
    expect(ids(rows)).toContain(OLLAMA_ANCHOR.id);
  });

  it("replaces the anchors with what the runner reports once coverage is known", () => {
    const coverage = live(
      status("ollama", { models: ["ollama/qwen3-coder:30b", "ollama/gpt-oss:20b"] }),
      status("opencode", { models: ["opencode/openai/gpt-5"] })
    );
    const rows = annotateModels(CATALOG, coverage);

    // The catalog's OpenCode anchor was not reported — gone. Its Ollama anchor WAS reported
    // — kept, with the catalog's metadata rather than a synthesized name.
    expect(ids(rows)).not.toContain(OPENCODE_ANCHOR.id);
    expect(byId(rows, OLLAMA_ANCHOR.id)).toMatchObject({
      name: "GPT-OSS 20B (catalog)",
      billing: "runner",
      reason: "runnerReady",
    });

    // Reported ids the catalog doesn't list are synthesized as runner-only chat rows.
    expect(byId(rows, "ollama/qwen3-coder:30b")).toMatchObject({
      name: "QWEN3 Coder 30B (Ollama)",
      provider: "Ollama",
      cli: "ollama",
      kind: "Chat",
      requiresRunner: true,
      description: "Installed locally via Ollama",
      billing: "runner",
      connected: true,
    });
    expect(byId(rows, "opencode/openai/gpt-5")).toMatchObject({
      name: "Openai · gpt-5 (OpenCode)",
      provider: "OpenCode",
      cli: "opencode",
      billing: "runner",
    });
  });

  it("drops the anchors when the runner reports nothing for those providers", () => {
    const rows = annotateModels(CATALOG, live(status("claude")));
    expect(ids(rows)).not.toContain(OLLAMA_ANCHOR.id);
    expect(ids(rows)).not.toContain(OPENCODE_ANCHOR.id);
  });

  it("keeps a saved discovered model the runner no longer reports, as unavailable", () => {
    const rows = annotateModels(
      CATALOG,
      live(status("ollama", { models: ["ollama/qwen3-coder:30b"] })),
      { currentModel: "ollama/llama3:8b" }
    );
    expect(byId(rows, "ollama/llama3:8b")).toMatchObject({
      name: "LLAMA3 8B (Ollama)",
      description: "Not currently pulled in Ollama",
      // Ollama itself is ready, but the model cannot run — the row stays only because it is
      // the saved value. Billing follows the provider; the description carries the reason.
      connected: true,
    });
  });

  it("keeps a saved discovered model whose provider is not ready at all", () => {
    const rows = annotateModels(CATALOG, live(status("claude")), {
      currentModel: "opencode/openai/gpt-5",
    });
    expect(byId(rows, "opencode/openai/gpt-5")).toMatchObject({
      description: "Not available from your current OpenCode logins",
      billing: "unavailable",
      reason: "runnerOnlyNotReady",
      connected: false,
    });
  });

  it("prettifies Ollama tags and leaves OpenCode ids verbatim", () => {
    expect(ollamaModelName("ollama/qwen3-coder:30b")).toBe("QWEN3 Coder 30B");
    expect(ollamaModelName("gpt-oss:latest")).toBe("Gpt Oss");
    expect(ollamaModelName("deepseek_r1")).toBe("Deepseek R1");
    expect(openCodeModelName("opencode/anthropic/claude-sonnet-4-6")).toBe(
      "Anthropic · claude-sonnet-4-6"
    );
    expect(openCodeModelName("opencode/bare")).toBe("bare");
  });
});

describe("billingForModel", () => {
  const coverage = live(status("claude"), status("codex", { authenticated: false }));
  const annotated = annotateModels(CATALOG, coverage);

  it("uses the annotated row when the id is listed", () => {
    expect(billingForModel("claude-opus-5", annotated, coverage)).toEqual({
      billing: "runner",
      reason: "runnerReady",
    });
    expect(billingForModel("GPT-5.6-SOL", annotated, coverage)).toEqual({
      billing: "runner",
      reason: "runnerWaitingSignIn",
    });
  });

  it("falls back to the id-prefix provider rule for ids the catalog does not list", () => {
    // Anthropic by default → ready on this runner.
    expect(billingForModel("claude-haiku-4-5", annotated, coverage)).toEqual({
      billing: "runner",
      reason: "runnerReady",
    });
    // OpenAI prefix → codex, logged out → waits on the runner for a sign-in.
    expect(billingForModel("gpt-5-mini", annotated, coverage)).toEqual({
      billing: "runner",
      reason: "runnerWaitingSignIn",
    });
    // Locally-discovered namespaces are only offered when the runner has them.
    expect(billingForModel("ollama/llama3:8b", annotated, coverage)).toEqual({
      billing: "unavailable",
      reason: "runnerOnlyNotReady",
    });
    expect(billingForModel("copilot/gpt-5", annotated, coverage)).toEqual({
      billing: "unavailable",
      reason: "runnerOnlyNotReady",
    });
    expect(billingForModel("opencode/openai/gpt-5", null, null)).toEqual({
      billing: "unavailable",
      reason: "runnerOnlyNotReady",
    });
  });

  it("honours missing coverage in the fallback", () => {
    expect(billingForModel("claude-haiku-4-5", null, null)).toEqual({
      billing: "runner",
      reason: "runnerOffline",
    });
  });
});
