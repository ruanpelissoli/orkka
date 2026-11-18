import { test } from "node:test";
import assert from "node:assert/strict";
import { features } from "../src/features.js";

test("baseline features are present", () => {
  assert.ok(features.includes("Answers /health"));
  assert.ok(features.includes("Lists its features at /features"));
});

test("no duplicates or empty entries", () => {
  assert.equal(new Set(features).size, features.length);
  assert.ok(features.every((f) => f.trim().length > 0));
});
