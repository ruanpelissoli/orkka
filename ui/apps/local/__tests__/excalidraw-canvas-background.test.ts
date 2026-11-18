import { describe, it, expect } from "vitest";
import { CANVAS_BACKGROUND_INPUT, canvasBackgroundFor } from "@/lib/excalidraw-skeleton";

/**
 * Excalidraw's dark theme paints the canvas through `invert(93%) hue-rotate(180deg)`,
 * so the colour we hand it is not the colour anyone sees. That trap cost a release: a
 * navy `#1a1a2e` arrived on screen as pale lavender and swallowed every agent-drawn
 * arrow. These tests re-run the filter, so a dark hex put back into the constant fails
 * here rather than in front of a user.
 */

/** The app's page background (`--color-surface-app`), which the canvas should match. */
const SURFACE_APP = [0x0b, 0x0f, 0x18];

/** CSS `hue-rotate(180deg)`: the spec matrix with cos = -1, sin = 0. */
const HUE_ROTATE_180 = [
  [-0.574, 1.43, 0.144],
  [0.426, 0.43, 0.144],
  [0.426, 1.43, -0.856],
];

function parseHex(hex: string): number[] {
  const h = hex.replace("#", "");
  return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
}

/** What Excalidraw's dark theme actually puts on screen for a given input colour. */
function renderedInDarkTheme(hex: string): number[] {
  // invert(93%): c' = c + 0.93 * (255 - 2c)
  const inverted = parseHex(hex).map((c) => c + 0.93 * (255 - 2 * c));
  return HUE_ROTATE_180.map((row) =>
    Math.min(255, Math.max(0, row.reduce((sum, k, i) => sum + k * inverted[i], 0)))
  );
}

const luminance = (rgb: number[]) => (rgb[0] + rgb[1] + rgb[2]) / 3;

describe("the canvas background survives the dark theme's filter", () => {
  it("renders dark, not light", () => {
    const rendered = renderedInDarkTheme(CANVAS_BACKGROUND_INPUT);

    expect(luminance(rendered)).toBeLessThan(40);
  });

  it("lands close to the app's page background", () => {
    const rendered = renderedInDarkTheme(CANVAS_BACKGROUND_INPUT);
    const distance = Math.hypot(...rendered.map((c, i) => c - SURFACE_APP[i]));

    expect(distance).toBeLessThan(15);
  });

  it("catches the mistake it exists for: a dark hex renders light", () => {
    // The value that shipped the bug.
    expect(luminance(renderedInDarkTheme("#1a1a2e"))).toBeGreaterThan(200);
  });
});

describe("canvasBackgroundFor", () => {
  it("supplies a background when the scene has none", () => {
    expect(canvasBackgroundFor(undefined)).toBe(CANVAS_BACKGROUND_INPUT);
  });

  it("replaces the backgrounds Orkka wrote itself, so old diagrams are fixed too", () => {
    expect(canvasBackgroundFor("#ffffff")).toBe(CANVAS_BACKGROUND_INPUT);
    expect(canvasBackgroundFor("#1a1a2e")).toBe(CANVAS_BACKGROUND_INPUT);
    expect(canvasBackgroundFor("#1A1A2E")).toBe(CANVAS_BACKGROUND_INPUT);
  });

  it("leaves a background the user picked alone", () => {
    expect(canvasBackgroundFor("#2b1a3d")).toBe("#2b1a3d");
  });
});
