import { describe, it, expect } from "vitest";
import { createPatch } from "diff";
import { parsePatch } from "@/components/pr/diff-parser";

// The document diff view pipes jsdiff's unified-patch output straight into the
// PR diff parser — this covers that contract.
describe("document revision diff", () => {
  it("maps a content change to add/delete lines", () => {
    const oldContent = "# Title\n\nline one\nline two\n";
    const newContent = "# Title\n\nline one changed\nline two\n";

    const hunks = parsePatch(createPatch("document.md", oldContent, newContent));

    expect(hunks).toHaveLength(1);
    const lines = hunks[0].lines;
    expect(lines.some((l) => l.type === "delete" && l.content === "line one")).toBe(true);
    expect(lines.some((l) => l.type === "add" && l.content === "line one changed")).toBe(true);
  });

  it("returns no hunks for identical content", () => {
    const content = "same\ncontent\n";
    const hunks = parsePatch(createPatch("document.md", content, content));
    expect(hunks).toHaveLength(0);
  });

  it("numbers added lines against the new revision", () => {
    const hunks = parsePatch(createPatch("document.md", "a\n", "a\nb\n"));
    const added = hunks[0].lines.find((l) => l.type === "add");
    expect(added?.newLineNumber).toBe(2);
    expect(added?.oldLineNumber).toBeNull();
  });
});
