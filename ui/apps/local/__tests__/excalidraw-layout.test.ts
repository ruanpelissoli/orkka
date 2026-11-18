import { describe, it, expect } from "vitest";
import { tidyShapes, wrapLabel, sizeForLabel, type LayoutBox } from "@/lib/excalidraw-layout";
import { normalizeSkeletonElements } from "@/lib/excalidraw-skeleton";

/**
 * The model sizes a box before it knows how long its own label is, so boxes arrive too
 * small, overlapping, and half a line out of true — on screen the text lies across the
 * diagram instead of inside the border. These tests hold the geometry to what a reader
 * needs: labels inside their box, rows square, nothing colliding, arrows touching the
 * boxes they name.
 */

const FONT = 20;
const LINE = FONT * 1.25;
const PAD = 5;

const box = (id: string, x: number, y: number, text?: string, w = 160, h = 80): LayoutBox &
  Record<string, unknown> => ({
  id,
  type: "rectangle",
  x,
  y,
  width: w,
  height: h,
  ...(text ? { label: { text } } : {}),
});

/** The height Excalidraw will need for this label once it wraps it to the box. */
function heightNeeded(text: string, innerWidth: number): number {
  return wrapLabel(text, innerWidth, FONT).length * LINE + 2 * PAD;
}

const overlaps = (a: LayoutBox, b: LayoutBox) =>
  a.x < b.x + b.width && b.x < a.x + a.width &&
  a.y < b.y + b.height && b.y < a.y + a.height;

describe("wrapLabel", () => {
  it("wraps on spaces", () => {
    expect(wrapLabel("alpha beta gamma delta", 120, FONT).length).toBeGreaterThan(1);
  });

  it("breaks a word that cannot fit on a line of its own", () => {
    const lines = wrapLabel("KubernetesCloudJobLauncher", 60, FONT);

    expect(lines.length).toBeGreaterThan(1);
    expect(lines.join("")).toBe("KubernetesCloudJobLauncher");
  });

  it("keeps the author's own line breaks", () => {
    expect(wrapLabel("first\nsecond", 400, FONT)).toEqual(["first", "second"]);
  });
});

describe("sizeForLabel", () => {
  it("keeps the model's width when the label fits it", () => {
    expect(sizeForLabel("ok", 160, FONT).width).toBe(160);
  });

  it("widens only as far as the longest unbreakable word", () => {
    const { width } = sizeForLabel("AgentRunCallbackHandler", 60, FONT);

    expect(width).toBeGreaterThan(60);
    expect(wrapLabel("AgentRunCallbackHandler", width - 2 * PAD, FONT)).toHaveLength(1);
  });

  it("gives the wrapped lines room to sit inside the box", () => {
    const text = "KubernetesCloudJobLauncher Job + Secret (1 MiB cap) activeDeadlineSeconds";
    const { width, height } = sizeForLabel(text, 160, FONT);

    expect(height).toBeGreaterThanOrEqual(heightNeeded(text, width - 2 * PAD));
  });
});

describe("tidyShapes", () => {
  it("grows a box until its label fits inside it", () => {
    const text = "KubernetesCloudJobLauncher Job + Secret (1 MiB cap) activeDeadlineSeconds";
    const shape = box("a", 0, 0, text);

    tidyShapes([shape]);

    expect(shape.height).toBeGreaterThanOrEqual(heightNeeded(text, shape.width - 2 * PAD));
    expect(shape.height).toBeGreaterThan(80);
  });

  it("leaves a box that already fits its label alone", () => {
    const shape = box("a", 40, 40, "API");

    tidyShapes([shape]);

    expect(shape).toMatchObject({ x: 40, y: 40, width: 160, height: 80 });
  });

  it("puts a row on one top line and one height, so arrows between them stay level", () => {
    const row = [box("a", 0, 0, "short"), box("b", 220, 6, "a much longer label that wraps"), box("c", 440, 3, "mid")];

    tidyShapes(row);

    expect(new Set(row.map((b) => b.y)).size).toBe(1);
    expect(new Set(row.map((b) => b.height)).size).toBe(1);
    // equal tops + equal heights means equal centres — the thing arrows are routed to
    expect(new Set(row.map((b) => b.y + b.height / 2)).size).toBe(1);
  });

  it("pushes apart boxes that overlap after growing", () => {
    const a = box("a", 0, 0, "AgentRunCallbackHandler per-run HMAC token 204 / 401 / 410");
    const b = box("b", 170, 0, "orkka-agent-runner Job clone / agent / CheckCommand");

    tidyShapes([a, b]);

    expect(overlaps(a, b)).toBe(false);
    expect(b.x).toBeGreaterThanOrEqual(a.x + a.width);
  });

  it("keeps rows clear of each other once they are taller", () => {
    const top = box("a", 0, 0, "a label long enough to need several lines of wrapping here");
    const bottom = box("b", 0, 100, "second row");

    tidyShapes([top, bottom]);

    expect(overlaps(top, bottom)).toBe(false);
    expect(bottom.y).toBeGreaterThanOrEqual(top.y + top.height);
  });

  it("re-wraps a boundary rectangle around its contents instead of shoving it aside", () => {
    const boundary = box("net", -40, -40, undefined, 500, 300);
    const inside = [box("a", 0, 0, "service one"), box("b", 240, 0, "service two")];

    tidyShapes([boundary, ...inside]);

    for (const child of inside) {
      expect(boundary.x).toBeLessThan(child.x);
      expect(boundary.y).toBeLessThan(child.y);
      expect(boundary.x + boundary.width).toBeGreaterThan(child.x + child.width);
      expect(boundary.y + boundary.height).toBeGreaterThan(child.y + child.height);
    }
  });

  it("ignores shapes the model gave no size", () => {
    const shape = { type: "rectangle", x: 0, y: 0 } as unknown as LayoutBox;

    tidyShapes([shape]);

    expect(shape).toEqual({ type: "rectangle", x: 0, y: 0 });
  });
});

describe("arrows follow the tidied geometry", () => {
  it("lands on the boxes' edges after they have grown and moved", () => {
    const elements = normalizeSkeletonElements([
      box("a", 0, 0, "TaskOrchestrator phase by lane (Worker pod)"),
      box("b", 220, 0, "KubernetesCloudJobLauncher Job + Secret (1 MiB cap)"),
      { type: "arrow", start: { id: "a" }, end: { id: "b" } },
    ]);

    const [a, b, arrow] = elements as unknown as [LayoutBox, LayoutBox, Record<string, number>];

    // The arrow starts clear of a's right edge and stops short of b's left edge.
    expect(arrow.x).toBeGreaterThanOrEqual(a.x + a.width);
    expect(arrow.x + arrow.width).toBeLessThanOrEqual(b.x);
    // Both boxes share a centre line, so the connector is horizontal.
    expect(arrow.height).toBe(0);
    expect(arrow.y).toBe(a.y + a.height / 2);
  });
});
