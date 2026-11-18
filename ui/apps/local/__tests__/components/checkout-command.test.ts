import { describe, it, expect } from "vitest";
import { checkoutCommand } from "@/lib/utils";

describe("checkoutCommand", () => {
  it("fetches before switching", () => {
    // Agent branches live on the forge — without the fetch, `git switch` fails for a
    // branch the developer's clone has never seen.
    expect(checkoutCommand("orkka/BRW-7")).toBe("git fetch origin && git switch orkka/BRW-7");
  });

  it("passes the branch name through untouched", () => {
    expect(checkoutCommand("orkka/task-01KXHVA/weird")).toContain("orkka/task-01KXHVA/weird");
  });
});
