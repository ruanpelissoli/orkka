import { describe, expect, it } from "vitest";
import { features } from "../app/features";

describe("features", () => {
  it("has at least the two baseline entries", () => {
    expect(features).toContain("Renders a heading");
    expect(features).toContain("Lists its features");
  });

  it("has no duplicates or empty entries", () => {
    expect(new Set(features).size).toBe(features.length);
    expect(features.every((f) => f.trim().length > 0)).toBe(true);
  });
});
