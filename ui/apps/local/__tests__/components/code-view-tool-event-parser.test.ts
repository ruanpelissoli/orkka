import { describe, it, expect } from "vitest";
import { parseToolEvent, relativizePath, shellWords } from "@/components/code-view/tool-event-parser";
import type { StreamEvent } from "@/hooks/use-task-signalr";

function toolUse(tool: string, input: unknown): StreamEvent {
  return {
    type: "ToolUse",
    content: `${tool}: ${JSON.stringify(input)}`,
    timestamp: "2026-08-18T10:00:00Z",
  };
}

describe("relativizePath", () => {
  it("cuts platform worktree roots", () => {
    expect(relativizePath("/repos/org1/repo2/worktrees/task-01ABC/src/app/page.tsx")).toBe(
      "src/app/page.tsx"
    );
  });

  it("cuts local-runner sibling worktree roots (Windows paths)", () => {
    expect(relativizePath("C:\\Projects\\waveborne\\game-worktrees\\WAV-27\\src\\Foo.cs")).toBe(
      "src/Foo.cs"
    );
  });

  it("leaves unknown layouts alone (forward slashes)", () => {
    expect(relativizePath("./src/index.ts")).toBe("src/index.ts");
    expect(relativizePath("C:\\other\\place\\a.ts")).toBe("C:/other/place/a.ts");
  });
});

describe("shellWords", () => {
  it("splits with quotes", () => {
    expect(shellWords(`rm -rf "a b/c.txt" 'd.txt' e\\ f`)).toEqual(["rm", "-rf", "a b/c.txt", "d.txt", "e f"]);
  });
});

describe("parseToolEvent", () => {
  it("ignores non-tool events and read-only tools", () => {
    expect(parseToolEvent({ type: "AgentThinking", content: "hi", timestamp: "" })).toBeNull();
    expect(parseToolEvent(toolUse("Read", { file_path: "/x/worktrees/t/a.ts" }))).toBeNull();
    expect(parseToolEvent(toolUse("Grep", { pattern: "x" }))).toBeNull();
    expect(parseToolEvent(toolUse("Bash", { command: "npm test" }))).toBeNull();
  });

  it("parses Edit with old/new strings", () => {
    const parsed = parseToolEvent(
      toolUse("Edit", {
        file_path: "/repos/o/r/worktrees/task-1/src/a.ts",
        old_string: "const a = 1;",
        new_string: "const a = 2;",
      })
    );
    expect(parsed).toEqual({
      tool: "Edit",
      timestamp: "2026-08-18T10:00:00Z",
      effects: [
        {
          kind: "edit",
          path: "src/a.ts",
          oldString: "const a = 1;",
          newString: "const a = 2;",
          replaceAll: false,
        },
      ],
    });
  });

  it("parses Write with content and MultiEdit into several edits", () => {
    const write = parseToolEvent(toolUse("Write", { file_path: "/w/worktrees/t/new.ts", content: "x\n" }));
    expect(write?.effects).toEqual([{ kind: "write", path: "new.ts", content: "x\n" }]);

    const multi = parseToolEvent(
      toolUse("MultiEdit", {
        file_path: "/w/worktrees/t/m.ts",
        edits: [
          { old_string: "a", new_string: "b" },
          { old_string: "c", new_string: "d", replace_all: true },
        ],
      })
    );
    expect(multi?.effects.map((e) => [e.kind, e.oldString, e.newString, e.replaceAll])).toEqual([
      ["edit", "a", "b", false],
      ["edit", "c", "d", true],
    ]);
  });

  it("derives effects from Bash commands", () => {
    const parsed = parseToolEvent(
      toolUse("Bash", {
        command:
          'git mv src/old.ts src/new.ts && rm -f "docs/gone.md" ; sed -i "s/a/b/" src/x.ts && echo hi > out/log.txt',
      })
    );
    expect(parsed?.effects).toEqual([
      { kind: "rename", path: "src/old.ts", newPath: "src/new.ts" },
      { kind: "delete", path: "docs/gone.md" },
      { kind: "dirty", path: "src/x.ts" },
      { kind: "dirty", path: "out/log.txt" },
    ]);
  });

  it("prefers structured metadata when present", () => {
    const parsed = parseToolEvent({
      type: "ToolUse",
      content: "Edit: not-json",
      timestamp: "t",
      metadata: { tool: "Edit", filePath: "/repos/o/r/worktrees/task-1/src/meta.ts" },
    });
    expect(parsed?.effects[0]).toMatchObject({ kind: "edit", path: "src/meta.ts" });
  });

  it("returns null for a mutating tool with no usable path", () => {
    expect(parseToolEvent(toolUse("Edit", {}))).toBeNull();
    expect(parseToolEvent({ type: "ToolUse", content: "Write", timestamp: "t" })).toBeNull();
  });
});
