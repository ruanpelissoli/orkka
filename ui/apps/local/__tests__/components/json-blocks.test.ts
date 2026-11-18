import { describe, it, expect } from "vitest";
import "../mocks/navigation";
import "../mocks/intl";
import "../mocks/i18n-navigation";
import { findJsonBlocks, opensFence } from "@/components/chat/QuestionBlocks";

/**
 * A fence marker is a block only at the start of a line. The 2026-09-07 knowledge-base reply
 * quoted the project's own CLAUDE.md ("via ```design blocks") INSIDE a document's JSON string
 * and the panel showed "Creating designs…" under a documents reply — while the real block,
 * still streaming, was rendered as raw JSON.
 */
describe("findJsonBlocks fence anchoring", () => {
  it("ignores a marker quoted inside a JSON string", () => {
    const content =
      "Saved.\n```document\n" +
      JSON.stringify({ documents: [{ title: "Rules", path: "", content: "Muse works via ```design blocks." }] }) +
      "\n```\nDone.";

    expect(findJsonBlocks(content, "```design")).toEqual({ ranges: [], inProgressStart: null });
    const docs = findJsonBlocks(content, "```document");
    expect(docs.ranges).toHaveLength(1);
    expect(docs.inProgressStart).toBeNull();
  });

  it("does not let ```document match a ```documents fence, and requires a line start", () => {
    expect(opensFence("```documents\n{}", "```document", 0)).toBe(false);
    expect(opensFence("x ```document\n{}", "```document", 2)).toBe(false);
    expect(opensFence("intro\n```document  \n{}", "```document", 6)).toBe(true);
    expect(opensFence("```document", "```document", 0)).toBe(true);
  });

  it("still reports a real unterminated block as in progress", () => {
    const content = 'Working.\n```document\n{"documents":[{"title":"A"';
    expect(findJsonBlocks(content, "```document").inProgressStart).toBe(9);
  });
});
