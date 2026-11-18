import { describe, it, expect } from "vitest";
import {
  duplicateEpicKeys,
  isValidBranchTemplate,
  isValidPollInterval,
  normalizeTrackerSubpath,
} from "@/lib/tracker";

/** Mirrors orkka-api's TrackerSubpath / SaveProjectTrackerValidator. */
describe("tracker rules", () => {
  describe("normalizeTrackerSubpath", () => {
    it.each([
      ["", null],
      ["   ", null],
      ["packages/api", "packages/api"],
      ["packages\\api", "packages/api"],
      ["packages//api/", "packages/api"],
      [" apps/web ", "apps/web"],
    ])("accepts %j as %j", (input, normalized) => {
      expect(normalizeTrackerSubpath(input)).toEqual({ ok: true, normalized });
    });

    it.each([
      "/packages/api",
      "\\packages\\api",
      "C:/code/api",
      "C:\\code",
      "~/code",
      "../outside",
      "packages/../../etc",
      "./packages",
      "packages/ api",
      "a".repeat(501),
    ])("refuses %j", (input) => {
      expect(normalizeTrackerSubpath(input).ok).toBe(false);
    });
  });

  it("requires {key} in a branch template, and allows blank for the default", () => {
    expect(isValidBranchTemplate("{type}/{key}-{slug}")).toBe(true);
    expect(isValidBranchTemplate("")).toBe(true);
    expect(isValidBranchTemplate("feature/{slug}")).toBe(false);
    expect(isValidBranchTemplate("{key}" + "x".repeat(200))).toBe(false);
  });

  it("bounds the poll interval to 60–300 seconds", () => {
    expect(isValidPollInterval(60)).toBe(true);
    expect(isValidPollInterval(300)).toBe(true);
    expect(isValidPollInterval(59)).toBe(false);
    expect(isValidPollInterval(301)).toBe(false);
    expect(isValidPollInterval(90.5)).toBe(false);
    expect(isValidPollInterval(Number.NaN)).toBe(false);
  });

  it("finds epics mapped twice, ignoring case", () => {
    expect(duplicateEpicKeys(["ACME-1", "acme-1", "ACME-2"])).toEqual(["acme-1"]);
    expect(duplicateEpicKeys(["ACME-1", "ACME-2"])).toEqual([]);
  });
});
