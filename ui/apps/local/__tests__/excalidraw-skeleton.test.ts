import { describe, it, expect } from "vitest";
import {
  normalizeSkeletonElements,
  sanitizeConvertedElements,
} from "@/lib/excalidraw-skeleton";

const rect = (id: string, x: number, y: number, w = 200, h = 80) => ({
  id,
  type: "rectangle",
  x,
  y,
  width: w,
  height: h,
});

describe("normalizeSkeletonElements", () => {
  it("routes an arrow with start/end refs edge-to-edge, ignoring model geometry", () => {
    const [, , arrow] = normalizeSkeletonElements([
      rect("a", 0, 0),
      rect("b", 400, 0),
      { type: "arrow", x: 999, y: 999, width: 1, height: 1, start: { id: "a" }, end: { id: "b" } },
    ]);

    // a's right edge is at x=200, b's left edge at x=400; centers share y=40
    expect(arrow.x).toBeGreaterThan(200);
    expect((arrow.x as number) + (arrow.width as number)).toBeLessThan(400);
    expect(arrow.y).toBe(40);
    expect(arrow.height).toBe(0);
    expect(arrow.start).toEqual({ id: "a" });
    expect(arrow.end).toEqual({ id: "b" });
  });

  it("gives an arrow with refs but no coordinates finite geometry", () => {
    const [, , arrow] = normalizeSkeletonElements([
      rect("a", 0, 0),
      rect("b", 0, 300),
      { type: "arrow", start: { id: "a" }, end: { id: "b" } },
    ]);

    expect(Number.isFinite(arrow.x)).toBe(true);
    expect(Number.isFinite(arrow.y)).toBe(true);
    expect(Number.isFinite(arrow.width)).toBe(true);
    expect(Number.isFinite(arrow.height)).toBe(true);
    // vertical flow: arrow points downward between the shapes
    expect(arrow.height as number).toBeGreaterThan(0);
  });

  it("defaults missing x/y to 0 and drops invalid width/height", () => {
    const [shape] = normalizeSkeletonElements([
      { type: "rectangle", width: "wide" as unknown as number },
    ]);

    expect(shape.x).toBe(0);
    expect(shape.y).toBe(0);
    expect("width" in shape).toBe(false);
  });

  it("drops dangling start/end refs so the converter is not confused", () => {
    const [, arrow] = normalizeSkeletonElements([
      rect("a", 0, 0),
      { type: "arrow", x: 100, y: 40, width: 50, height: 0, start: { id: "a" }, end: { id: "ghost" } },
    ]);

    expect(arrow.start).toEqual({ id: "a" });
    expect("end" in arrow).toBe(false);
  });

  it("leaves standalone arrows with valid geometry untouched", () => {
    const [arrow] = normalizeSkeletonElements([
      { type: "arrow", x: 10, y: 20, width: 100, height: 0 },
    ]);

    expect(arrow).toMatchObject({ x: 10, y: 20, width: 100, height: 0 });
  });

  it("keeps overlapping shapes' arrows valid (no NaN from zero-length route)", () => {
    const [, , arrow] = normalizeSkeletonElements([
      rect("a", 0, 0),
      rect("b", 0, 0),
      { type: "arrow", start: { id: "a" }, end: { id: "b" } },
    ]);

    expect(Number.isFinite(arrow.x)).toBe(true);
    expect(Number.isFinite(arrow.y)).toBe(true);
  });
});

describe("sanitizeConvertedElements", () => {
  it("drops elements with non-finite geometry", () => {
    const kept = sanitizeConvertedElements([
      { type: "rectangle", x: 0, y: 0, width: 10, height: 10 },
      { type: "arrow", x: NaN, y: NaN, width: 100, height: 0 },
      { type: "ellipse", x: 5, y: 5, width: Infinity, height: 10 },
    ]);

    expect(kept).toHaveLength(1);
    expect(kept[0].type).toBe("rectangle");
  });
});
