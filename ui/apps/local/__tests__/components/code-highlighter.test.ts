import { describe, it, expect } from "vitest";
import {
  highlightToLines,
  plainLines,
  langForPath,
  langForAlias,
  MAX_HIGHLIGHT_CHARS,
} from "@/components/code";

describe("langForPath", () => {
  it("maps extensions, either path separator", () => {
    expect(langForPath("apps/local/components/Foo.tsx")).toBe("tsx");
    expect(langForPath("src\\Orkka.Api\\Program.cs")).toBe("csharp");
    expect(langForPath("infra/main.tf")).toBe("hcl");
    expect(langForPath("packages/i18n/messages/en/dashboard.json")).toBe("json");
    expect(langForPath("README.md")).toBe("markdown");
    expect(langForPath("scripts/deploy.ps1")).toBe("powershell");
  });

  it("recognises telling filenames and dotfiles", () => {
    expect(langForPath("Dockerfile")).toBe("dockerfile");
    expect(langForPath("docker/Dockerfile.worker")).toBe("dockerfile");
    expect(langForPath("Makefile")).toBe("makefile");
    expect(langForPath(".env.local")).toBe("dotenv");
    expect(langForPath(".gitignore")).toBe("bash");
  });

  it("returns null when there is no grammar", () => {
    expect(langForPath("LICENSE")).toBeNull();
    expect(langForPath("assets/logo.png")).toBeNull();
    expect(langForPath("")).toBeNull();
    expect(langForPath(null)).toBeNull();
  });
});

describe("langForAlias", () => {
  it("resolves markdown fence info strings", () => {
    expect(langForAlias("ts")).toBe("typescript");
    expect(langForAlias("c#")).toBe("csharp");
    expect(langForAlias("shell")).toBe("bash");
    expect(langForAlias("yml")).toBe("yaml");
    expect(langForAlias("Dockerfile")).toBe("dockerfile");
    expect(langForAlias("tsx title=Foo.tsx")).toBe("tsx");
    expect(langForAlias("js {1,3}")).toBe("javascript");
  });

  it("passes grammar keys through and rejects unknowns", () => {
    expect(langForAlias("typescript")).toBe("typescript");
    expect(langForAlias("text")).toBeNull();
    expect(langForAlias("plaintext")).toBeNull();
    expect(langForAlias("")).toBeNull();
    expect(langForAlias(undefined)).toBeNull();
  });
});

describe("highlightToLines", () => {
  it("returns exactly one HTML string per input line, in order", async () => {
    const code = 'import { x } from "y";\n\nexport const n = 42; // answer\n';
    const lines = await highlightToLines(code, "typescript");
    expect(lines).not.toBeNull();
    expect(lines).toHaveLength(code.split("\n").length);
    expect(lines![1]).toBe("");
    // The keyword is coloured, the comment is italic, the number is amber
    expect(lines![0]).toMatch(/<span style="color:#c4b5fd">import<\/span>/);
    expect(lines![2]).toMatch(/color:#fcd34d">42/);
    expect(lines![2]).toMatch(/font-style:italic/);
  });

  it("escapes markup in token text", async () => {
    const lines = await highlightToLines('const a = "<b>&</b>";', "javascript");
    expect(lines![0]).not.toMatch(/<b>/);
    expect(lines![0]).toContain("&lt;b&gt;&amp;&lt;/b&gt;");
  });

  it("normalises CRLF and keeps the line count stable", async () => {
    const lines = await highlightToLines("a = 1\r\nb = 2\r\n", "python");
    expect(lines).toHaveLength(3);
  });

  it("gives up on oversized input", async () => {
    const big = "x".repeat(MAX_HIGHLIGHT_CHARS + 1);
    expect(await highlightToLines(big, "typescript")).toBeNull();
  });

  it("loads several grammars into the same highlighter", async () => {
    const cs = await highlightToLines("public sealed class Foo { }", "csharp");
    const py = await highlightToLines("def foo():\n    return None", "python");
    expect(cs![0]).toMatch(/color:#c4b5fd">public/);
    expect(py![1]).toMatch(/color:#c4b5fd">return/);
    expect(py![1]).toMatch(/color:#fcd34d">None/);
  });
});

describe("plainLines", () => {
  it("escapes and splits like the highlighter", () => {
    expect(plainLines("a<b\r\nc")).toEqual(["a&lt;b", "c"]);
  });
});
