/**
 * Helpers for agent-authored Excalidraw "skeleton" diagrams (source
 * "orkka-archie-skeleton"). Skeletons are expanded to full elements with
 * convertToExcalidrawElements(), but the converter trusts the input geometry:
 * an arrow without numeric x/y converts to x=NaN and a single non-finite
 * element breaks the whole canvas (scene bounds, zoom-to-fit, rendering).
 * The model's arrow coordinates are also unreliable, so arrows that reference
 * shapes by id are re-routed edge-to-edge here before conversion.
 */

/**
 * The canvas colour handed to Excalidraw -- NOT the colour you see.
 *
 * Excalidraw's dark theme renders the canvas through `invert(93%) hue-rotate(180deg)`,
 * so whatever goes in comes out roughly inverted: the navy `#1a1a2e` we used to pass
 * arrived on screen as the pale lavender `#d4d4e6`, and agent-drawn arrows -- authored
 * near-black, and so inverted to near-white -- disappeared into it.
 *
 * This value is the pre-image of the app's page background under that filter: it renders
 * as `#0d1515`, the closest the filter can land to `--color-surface-app` (`#0b0f18`).
 * A saturated navy is not reachable -- outputs that dark need a near-white input, which
 * leaves almost no room for hue -- but at this luminance the difference is invisible,
 * and dark is the part that matters: it puts the inverted (light) strokes back on a
 * ground they contrast with.
 *
 * Pass a DARK colour here and the canvas goes light again. See canvasBackgroundFor().
 */
export const CANVAS_BACKGROUND_INPUT = "#f6ffff";

/**
 * Backgrounds Orkka itself has written into saved scenes: Excalidraw's own default, and
 * the navy that turned out to be the bug above. Neither is a choice a user made, so both
 * are replaced -- otherwise every diagram drawn before this fix keeps its pale canvas.
 */
const ORKKA_WRITTEN_BACKGROUNDS = new Set(["#ffffff", "#1a1a2e"]);

/**
 * The background to open a scene with: the user's own pick when they made one, ours
 * otherwise.
 */
export function canvasBackgroundFor(savedBackground: string | undefined): string {
  if (!savedBackground) return CANVAS_BACKGROUND_INPUT;
  return ORKKA_WRITTEN_BACKGROUNDS.has(savedBackground.toLowerCase())
    ? CANVAS_BACKGROUND_INPUT
    : savedBackground;
}

import { tidyShapes, type LayoutBox } from "./excalidraw-layout";

export interface SkeletonScene {
  source?: string;
  elements?: Record<string, unknown>[];
  appState?: Record<string, unknown>;
}

type Skeleton = Record<string, unknown> & {
  id?: unknown;
  type?: unknown;
  x?: unknown;
  y?: unknown;
  width?: unknown;
  height?: unknown;
  start?: { id?: unknown };
  end?: { id?: unknown };
};

const ARROW_GAP = 8;
const DEFAULT_SHAPE_SIZE = { width: 160, height: 80 };

const isFinite = Number.isFinite;

function center(el: Skeleton): { x: number; y: number; halfW: number; halfH: number } {
  const w = isFinite(el.width) ? (el.width as number) : DEFAULT_SHAPE_SIZE.width;
  const h = isFinite(el.height) ? (el.height as number) : DEFAULT_SHAPE_SIZE.height;
  return {
    x: (el.x as number) + w / 2,
    y: (el.y as number) + h / 2,
    halfW: w / 2,
    halfH: h / 2,
  };
}

/**
 * Route a connector between two shapes: clip the center-to-center line at
 * each shape's bounding box (plus a small gap) so the arrow visibly links
 * them regardless of what geometry the model supplied.
 */
function routeBetween(
  from: ReturnType<typeof center>,
  to: ReturnType<typeof center>
): { x: number; y: number; width: number; height: number } | null {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const len = Math.hypot(dx, dy);
  if (len < 1) return null;

  // Parametric t (0=from center, 1=to center) where the line crosses each bbox
  const exitT = Math.min(
    dx !== 0 ? from.halfW / Math.abs(dx) : Infinity,
    dy !== 0 ? from.halfH / Math.abs(dy) : Infinity
  );
  const entryT = 1 - Math.min(
    dx !== 0 ? to.halfW / Math.abs(dx) : Infinity,
    dy !== 0 ? to.halfH / Math.abs(dy) : Infinity
  );

  const gapT = ARROW_GAP / len;
  let t0 = exitT + gapT;
  let t1 = entryT - gapT;

  // Two wide boxes standing close together leave less room between them than the gap
  // wants. Fixed 0.45/0.55 fallbacks used to be applied here, but on that geometry those
  // parameters are INSIDE both shapes, so the arrow was drawn buried in the boxes it was
  // meant to join. Give up the gap first, and the arrow only when the shapes actually
  // touch -- there is no honest line to draw between them then.
  if (t1 <= t0) {
    t0 = exitT;
    t1 = entryT;
  }
  if ((t1 - t0) * len < 1) return null;

  const x = from.x + dx * t0;
  const y = from.y + dy * t0;
  return { x, y, width: dx * (t1 - t0), height: dy * (t1 - t0) };
}

/**
 * Make skeleton elements safe for convertToExcalidrawElements:
 * - shapes are grown to fit their labels and squared up into rows (see tidyShapes);
 * - arrows/lines with resolvable start/end ids get computed edge-to-edge
 *   geometry (the model's rough coordinates are ignored);
 * - every element gets finite x/y; invalid width/height are dropped so the
 *   converter's defaults kick in.
 *
 * Order matters: the tidy pass moves shapes, so arrows are routed from where the
 * shapes end up, not from where the model imagined them.
 */
export function normalizeSkeletonElements(
  elements: Record<string, unknown>[]
): Record<string, unknown>[] {
  const out = (elements as Skeleton[]).map((el) => {
    const copy: Skeleton = { ...el };

    if (!isFinite(copy.x)) copy.x = 0;
    if (!isFinite(copy.y)) copy.y = 0;
    if (copy.width !== undefined && !isFinite(copy.width)) delete copy.width;
    if (copy.height !== undefined && !isFinite(copy.height)) delete copy.height;

    return copy;
  });

  const shapes = out.filter((el) => el.type !== "arrow" && el.type !== "line");
  tidyShapes(shapes as unknown as LayoutBox[]);

  const shapesById = new Map<string, Skeleton>();
  for (const shape of shapes) {
    if (typeof shape.id === "string") shapesById.set(shape.id, shape);
  }

  for (const el of out) {
    if (el.type !== "arrow" && el.type !== "line") continue;

    const startShape =
      typeof el.start?.id === "string" ? shapesById.get(el.start.id) : undefined;
    const endShape =
      typeof el.end?.id === "string" ? shapesById.get(el.end.id) : undefined;

    if (startShape && endShape && isFinite(startShape.x) && isFinite(startShape.y) &&
        isFinite(endShape.x) && isFinite(endShape.y)) {
      const routed = routeBetween(center(startShape), center(endShape));
      if (routed) Object.assign(el, routed);
    }
    // Drop refs the converter can't resolve — a dangling id is harmless,
    // but this keeps the intent explicit.
    if (el.start && !startShape) delete el.start;
    if (el.end && !endShape) delete el.end;
  }

  return out;
}

/**
 * Post-conversion safety net: one element with non-finite geometry breaks
 * Excalidraw's scene bounds and rendering, so drop any that slipped through.
 */
export function sanitizeConvertedElements<T extends Record<string, unknown>>(
  elements: readonly T[]
): T[] {
  return elements.filter(
    (el) =>
      isFinite(el.x) && isFinite(el.y) && isFinite(el.width) && isFinite(el.height)
  );
}
