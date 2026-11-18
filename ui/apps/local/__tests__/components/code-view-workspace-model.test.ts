import { describe, it, expect } from "vitest";
import { emptyModel, withBaseline, applyToolEvent, totals } from "@/components/code-view/workspace-model";
import type { ParsedToolEvent } from "@/components/code-view/tool-event-parser";
import type { PrFile } from "@/lib/types";

const prFile = (over: Partial<PrFile> & { filename: string }): PrFile => ({
  status: "modified",
  additions: 3,
  deletions: 1,
  patch: "@@ -1,2 +1,4 @@\n a\n+b\n+c\n-d\n+e",
  previousFilename: null,
  ...over,
});

const edit = (path: string, at: string, oldString = "x", newString = "y\ny"): ParsedToolEvent => ({
  tool: "Edit",
  timestamp: at,
  effects: [{ kind: "edit", path, oldString, newString }],
});

describe("workspace model", () => {
  it("adds unknown files from live effects and tracks the agent cursor", () => {
    let m = emptyModel();
    m = applyToolEvent(m, edit("src/a.ts", "2026-08-18T10:00:00Z"));
    m = applyToolEvent(m, {
      tool: "Write",
      timestamp: "2026-08-18T10:00:05Z",
      effects: [{ kind: "write", path: "src/new.ts", content: "l1\nl2\n" }],
    });
    expect(Object.keys(m.files).sort()).toEqual(["src/a.ts", "src/new.ts"]);
    expect(m.files["src/a.ts"].status).toBe("modified");
    expect(m.files["src/new.ts"].status).toBe("added");
    expect(m.files["src/new.ts"].liveAdditions).toBe(2);
    expect(m.activePath).toBe("src/new.ts");
    expect(m.revision).toBe(2);
    expect(totals(m)).toEqual({ fileCount: 2, additions: 4, deletions: 1, hasLive: true });
  });

  it("returns the same model for a no-op event", () => {
    const m = emptyModel();
    const same = applyToolEvent(m, { tool: "Bash", timestamp: "t", effects: [{ kind: "rename", path: "a" }] });
    expect(same).toBe(m);
  });

  it("baseline replaces files and keeps only live edits newer than the capture", () => {
    let m = emptyModel();
    m = applyToolEvent(m, edit("src/old-edit.ts", "2026-08-18T09:00:00Z"));
    m = applyToolEvent(m, edit("src/a.ts", "2026-08-18T11:00:00Z"));
    m = withBaseline(m, [prFile({ filename: "src/a.ts" }), prFile({ filename: "src/b.ts", status: "added" })], {
      source: "snapshot",
      branchName: "feat/x",
      baseBranch: "main",
      capturedAt: "2026-08-18T10:00:00Z",
      truncated: false,
      revision: 0,
    });
    // old-edit predates the capture → folded into the baseline (which doesn't list it → gone)
    expect(m.files["src/old-edit.ts"]).toBeUndefined();
    // a.ts: baseline numbers + the newer live edit preserved on top
    expect(m.files["src/a.ts"].patch).not.toBeNull();
    expect(m.files["src/a.ts"].additions).toBe(3);
    expect(m.files["src/a.ts"].liveEdits).toHaveLength(1);
    expect(m.files["src/b.ts"].status).toBe("added");
    expect(m.baseline.source).toBe("snapshot");
    expect(m.activePath).toBe("src/a.ts");
  });

  it("renames move the entry and deletes mark removed", () => {
    let m = emptyModel();
    m = applyToolEvent(m, edit("src/a.ts", "2026-08-18T10:00:00Z"));
    m = applyToolEvent(m, {
      tool: "Bash",
      timestamp: "2026-08-18T10:00:01Z",
      effects: [
        { kind: "rename", path: "src/a.ts", newPath: "src/z.ts" },
        { kind: "delete", path: "src/gone.ts" },
      ],
    });
    expect(m.files["src/a.ts"]).toBeUndefined();
    expect(m.files["src/z.ts"].previousPath).toBe("src/a.ts");
    expect(m.files["src/z.ts"].status).toBe("renamed");
    expect(m.files["src/gone.ts"].status).toBe("removed");
    expect(m.activePath).toBe("src/gone.ts");
  });
});
