import { describe, it, expect } from "vitest";
import {
  groupModelsByProvider,
  providerFromModelId,
} from "@/lib/utils";
import { providerLabel } from "@/lib/constants";
import type { AiModel } from "@/lib/types";

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

describe("groupModelsByProvider", () => {
  it("orders by sortOrder and keeps each provider as one contiguous group", () => {
    // Deliberately interleaved input: sorting by sortOrder alone would split "Anthropic"
    // into two groups and shatter the <optgroup>s.
    const groups = groupModelsByProvider([
      model({ id: "b", sortOrder: 20 }),
      model({ id: "g1", provider: "Google", sortOrder: 30 }),
      model({ id: "a", sortOrder: 10 }),
      model({ id: "g2", provider: "Google", sortOrder: 40 }),
    ]);

    expect(groups.map(([provider]) => provider)).toEqual(["Anthropic", "Google"]);
    expect(groups[0][1].map((m) => m.id)).toEqual(["a", "b"]);
    expect(groups[1][1].map((m) => m.id)).toEqual(["g1", "g2"]);
  });

  it("preserves input order when no sortOrder is present", () => {
    const groups = groupModelsByProvider([
      { id: "x", provider: "OpenAI" },
      { id: "y", provider: "Anthropic" },
    ]);

    expect(groups.map(([provider]) => provider)).toEqual(["OpenAI", "Anthropic"]);
  });
});

describe("providerFromModelId", () => {
  it("mirrors the backend's prefix rule", () => {
    // Copilot and Ollama are namespaced precisely because their bare model names would
    // match the vendor prefixes below them.
    expect(providerFromModelId("copilot/gpt-5")).toBe("GitHubCopilot");
    expect(providerFromModelId("ollama/qwen3-coder:30b")).toBe("Ollama");
    expect(providerFromModelId("pixellab-pixflux")).toBe("PixelLab");
    expect(providerFromModelId("gemini-3-pro-preview")).toBe("Google");
    expect(providerFromModelId("gpt-5.5-codex")).toBe("OpenAI");
    expect(providerFromModelId("o4-mini")).toBe("OpenAI");
    expect(providerFromModelId("claude-opus-5")).toBe("Anthropic");
    expect(providerFromModelId("grok-4.6")).toBe("XAI");
    // Unknown ids fall back to Anthropic, matching the platform's historical behavior.
    expect(providerFromModelId("who-knows")).toBe("Anthropic");
    expect(providerFromModelId("")).toBe("Anthropic");
  });

  // OpenCode is a HARNESS, so its ids embed the vendor it drives. The prefix must win over
  // the embedded vendor or the UI would label (and the backend would route) these as the
  // inner vendor and bypass opencode entirely.
  it("resolves OpenCode ids by the harness, not the vendor named inside them", () => {
    expect(providerFromModelId("opencode/anthropic/claude-sonnet-4-6")).toBe("OpenCode");
    expect(providerFromModelId("opencode/openai/gpt-5.6-sol")).toBe("OpenCode");
    expect(providerFromModelId("opencode/xai/grok-4.6")).toBe("OpenCode");
    expect(providerFromModelId("OPENCODE/GOOGLE/GEMINI-3-PRO")).toBe("OpenCode");
  });
});

describe("providerLabel", () => {
  it("labels known providers and passes unknown ones through", () => {
    expect(providerLabel("GitHubCopilot")).toBe("GitHub Copilot");
    expect(providerLabel("Anthropic")).toBe("Claude");
    expect(providerLabel("XAI")).toBe("Grok (xAI)");
    expect(providerLabel("OpenCode")).toBe("OpenCode (any provider)");
    expect(providerLabel("SomeNewVendor")).toBe("SomeNewVendor");
  });
});
