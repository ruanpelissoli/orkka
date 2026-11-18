import { describe, it, expect } from "vitest";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { signInHint } from "@/components/runner/ProviderSignInList";

describe("signInHint", () => {
  it("gives each mode the exact command to run", () => {
    expect(signInHint("claude", "login", null)).toEqual({ key: "claudeLogin", command: "claude" });
    expect(signInHint("claude", "none", null)?.command).toBe("claude");
    expect(signInHint("claude", "token", "CLAUDE_CODE_OAUTH_TOKEN")?.command).toBe("claude setup-token");
    expect(signInHint("claude", "api-key", "ANTHROPIC_API_KEY")?.command).toBe(
      "./orkka workspace set-env <workspace> ANTHROPIC_API_KEY"
    );
    expect(signInHint("claude", "vertex", "global")?.key).toBe("claudeVertex");
    expect(signInHint("codex", "login", "ChatGPT")?.command).toBe("codex login");
    expect(signInHint("codex", "azure-openai", "acme.openai.azure.com")?.command).toContain("AZURE_OPENAI_API_KEY");
    expect(signInHint("copilot", "none", null)?.command).toBe("copilot");
    expect(signInHint("copilot", "token", "GH_TOKEN")?.command).toContain("COPILOT_GITHUB_TOKEN");
    expect(signInHint("copilot", "enterprise-host", "acme.ghe.com")?.command).toBe(
      "copilot login --host https://acme.ghe.com"
    );
  });

  it("has no hint for CLIs Orkka cannot guard", () => {
    expect(signInHint("gemini", "none", null)).toBeNull();
  });

  it("never puts a secret value in a command — set-env asks for it hidden", () => {
    for (const mode of ["api-key", "token"] as const) {
      const hint = signInHint("codex", mode, "CODEX_API_KEY");
      expect(hint?.command).not.toMatch(/=\S/);
    }
  });
});
