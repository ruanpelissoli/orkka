// @vitest-environment node
// A build-config module: it reads the file system, so it runs in Node, not happy-dom.
import { describe, it, expect } from "vitest";
import { resolveAppAlias } from "../../vite.aliases";

describe("the @/* resolver (vite.aliases.ts)", () => {
  it("tries the app first, then packages/ui, then packages/core, like tsconfig", () => {
    expect(resolveAppAlias("@/lib/actions")).toMatch(/\/apps\/local\/lib\/actions\.ts$/);
    expect(resolveAppAlias("@/components/inbox/InboxPageClient")).toMatch(
      /\/packages\/ui\/src\/components\/inbox\/InboxPageClient\.tsx$/
    );
    expect(resolveAppAlias("@/lib/api")).toMatch(/\/packages\/core\/src\/lib\/api\.ts$/);
    expect(resolveAppAlias("@/components/page")).toMatch(/\/apps\/local\/components\/page\/index\.ts$/);
    expect(resolveAppAlias("@/no/such/module")).toBeNull();
    expect(resolveAppAlias("react")).toBeNull();
  });

  it("returns forward-slash ids, so one file is one module (a second copy splits React contexts)", () => {
    expect(resolveAppAlias("@/components/providers/ProjectProvider")).not.toContain("\\");
  });
});
