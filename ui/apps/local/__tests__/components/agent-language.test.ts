import { describe, it, expect } from "vitest";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { selectedAgentLanguage } from "@/components/project/ProjectLanguageSetting";

/**
 * A project with no stored agent language used to render as the CURRENT UI LOCALE, which
 * stated a language the project had never been given — and made the fix unreachable, since
 * choosing the language already shown fires no change event. On 2026-09-08 that page read
 * "English" while Luna answered in Portuguese: the server's fallback is the locale the
 * BROWSER asks for (Accept-Language), not the one the dashboard is rendered in.
 */
describe("agent language selection", () => {
  it("shows an unset project as automatic, never as a language", () => {
    expect(selectedAgentLanguage(null)).toBe("");
    expect(selectedAgentLanguage(undefined)).toBe("");
    expect(selectedAgentLanguage("")).toBe("");
  });

  it("shows a stored language as itself", () => {
    expect(selectedAgentLanguage("en")).toBe("en");
    expect(selectedAgentLanguage("pt")).toBe("pt");
  });

  it("treats a value the picker cannot offer as unset", () => {
    // The API normalizes on save, so this is only reachable from older or hand-edited data —
    // showing "automatic" is honest there, while showing English would not be.
    expect(selectedAgentLanguage("pt-BR")).toBe("");
    expect(selectedAgentLanguage("nl")).toBe("");
  });
});
