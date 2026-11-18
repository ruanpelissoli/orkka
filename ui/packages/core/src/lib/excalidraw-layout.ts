/**
 * Tidies an agent-authored diagram before Excalidraw renders it.
 *
 * The model places shapes on a rough grid and writes a label into each one, but it has
 * no idea how wide its own words are: Excalidraw wraps a bound label to the container's
 * width and lets it spill out the bottom, so "KubernetesCloudJobLauncher / Job + Secret
 * / activeDeadlineSeconds" in a 140px-tall box renders as text lying across the diagram.
 * The same guesswork leaves boxes overlapping and rows half a line out of true.
 *
 * So the geometry is recomputed here rather than trusted: every box grows to fit its own
 * label, each row is squared up to a common top and height, and anything still colliding
 * is pushed apart. Arrows are routed afterwards, from the settled geometry -- routing
 * first would aim them at boxes that are no longer there.
 *
 * Two things are deliberately left alone: a shape whose size the model never supplied
 * (the converter's defaults own that case), and a "boundary" rectangle drawn around
 * other shapes -- pushing those apart would dismantle exactly the grouping they exist to
 * express, so they are re-fitted around their contents at the end instead.
 */

/** Excalidraw's font size for a bound label ("M"). */
const DEFAULT_LABEL_FONT_SIZE = 20;

/** Excalifont's line height. */
const LINE_HEIGHT = 1.25;

/** BOUND_TEXT_PADDING — Excalidraw's padding between a label and its container, per side. */
const LABEL_PADDING = 5;

/** Breathing room kept between neighbouring shapes, and the room an arrow needs. */
const MIN_GAP = 32;

/** Padding kept between a boundary rectangle and the shapes it encloses. */
const CONTAINER_PADDING = 24;

export interface LayoutBox {
  x: number;
  y: number;
  width: number;
  height: number;
  label?: { text?: unknown } | undefined;
  fontSize?: unknown;
}

/**
 * Per-character advance for Excalifont, as a fraction of the font size.
 *
 * Excalidraw measures text on a canvas with the real font; we cannot, because this runs
 * before the scene is mounted (and in tests with no canvas at all). These are measured
 * averages, rounded UP: a box slightly wider than it needs to be looks like a choice,
 * whereas one a few pixels short puts the text back outside the border.
 */
const NARROW_CHARS = new Set([..."iljtfrI.,:;'`|!()[]{}-"]);
const WIDE_CHARS = new Set([..."MWmw@%"]);

function advanceOf(char: string, fontSize: number): number {
  if (char === " ") return fontSize * 0.28;
  if (NARROW_CHARS.has(char)) return fontSize * 0.34;
  if (WIDE_CHARS.has(char)) return fontSize * 0.78;
  if (char >= "A" && char <= "Z") return fontSize * 0.64;
  return fontSize * 0.56;
}

function measure(text: string, fontSize: number): number {
  let width = 0;
  for (const char of text) width += advanceOf(char, fontSize);
  return width;
}

/**
 * Wraps the way Excalidraw does: on spaces, and mid-word when a single word is wider
 * than the line.
 */
export function wrapLabel(text: string, innerWidth: number, fontSize: number): string[] {
  const lines: string[] = [];

  for (const paragraph of text.split("\n")) {
    let line = "";

    for (const word of paragraph.split(" ")) {
      let remaining = word;

      // A word too long for an empty line is broken rather than allowed to overhang.
      while (measure(remaining, fontSize) > innerWidth) {
        if (line) {
          lines.push(line);
          line = "";
        }
        let cut = 1;
        while (cut < remaining.length && measure(remaining.slice(0, cut + 1), fontSize) <= innerWidth)
          cut++;
        lines.push(remaining.slice(0, cut));
        remaining = remaining.slice(cut);
      }

      const candidate = line ? `${line} ${remaining}` : remaining;
      if (line && measure(candidate, fontSize) > innerWidth) {
        lines.push(line);
        line = remaining;
      } else {
        line = candidate;
      }
    }

    lines.push(line);
  }

  return lines;
}

function labelTextOf(box: LayoutBox): string | null {
  const text = box.label?.text;
  return typeof text === "string" && text.trim() !== "" ? text : null;
}

/**
 * The smallest box that holds a label. The width the model chose is kept unless a single
 * unbreakable word needs more; the height is whatever the wrapped lines actually need.
 */
export function sizeForLabel(
  text: string,
  currentWidth: number,
  fontSize: number
): { width: number; height: number } {
  const longestWord = Math.max(
    ...text.split(/[\s\n]+/).map((word) => measure(word, fontSize)),
    0
  );
  const innerWidth = Math.max(currentWidth - 2 * LABEL_PADDING, longestWord, fontSize);
  const lines = wrapLabel(text, innerWidth, fontSize);

  return {
    width: Math.ceil(innerWidth + 2 * LABEL_PADDING),
    height: Math.ceil(lines.length * fontSize * LINE_HEIGHT + 2 * LABEL_PADDING),
  };
}

const contains = (outer: LayoutBox, inner: LayoutBox) =>
  outer !== inner &&
  outer.x <= inner.x &&
  outer.y <= inner.y &&
  outer.x + outer.width >= inner.x + inner.width &&
  outer.y + outer.height >= inner.y + inner.height;

/** Groups shapes into rows by vertical overlap, on the geometry the model authored. */
function groupIntoRows(boxes: LayoutBox[]): LayoutBox[][] {
  const rows: LayoutBox[][] = [];

  for (const box of [...boxes].sort((a, b) => a.y - b.y || a.x - b.x)) {
    const row = rows[rows.length - 1];
    if (row) {
      const top = Math.min(...row.map((b) => b.y));
      const bottom = Math.max(...row.map((b) => b.y + b.height));
      const overlap = Math.min(bottom, box.y + box.height) - Math.max(top, box.y);
      if (overlap >= box.height * 0.5) {
        row.push(box);
        continue;
      }
    }
    rows.push([box]);
  }

  return rows;
}

/**
 * Grows boxes to fit their labels, squares up rows, and separates anything that still
 * collides. Mutates the boxes in place; returns nothing so callers cannot mistake this
 * for a pure transform.
 */
export function tidyShapes(shapes: LayoutBox[]): void {
  const sized = shapes.filter(
    (s) => Number.isFinite(s.x) && Number.isFinite(s.y) &&
      Number.isFinite(s.width) && Number.isFinite(s.height)
  );

  // Boundary rectangles are laid out by their contents, not alongside them. Who encloses
  // whom is decided here, on the authored geometry: once the passes below have moved
  // things, a boundary no longer encloses anything and the relationship is unrecoverable.
  const boundaries = sized.filter((s) => sized.some((other) => contains(s, other)));
  const enclosed = new Map<LayoutBox, LayoutBox[]>(
    boundaries.map((boundary) => [
      boundary,
      sized.filter((s) => !boundaries.includes(s) && contains(boundary, s)),
    ])
  );
  const rows = groupIntoRows(sized.filter((s) => !boundaries.includes(s)));

  for (const row of rows) {
    for (const box of row) {
      const text = labelTextOf(box);
      if (!text) continue;

      const fontSize = Number.isFinite(box.fontSize)
        ? (box.fontSize as number)
        : DEFAULT_LABEL_FONT_SIZE;
      const needed = sizeForLabel(text, box.width, fontSize);

      // Grow around the centre so a box stays where the model put it.
      if (needed.width > box.width) {
        box.x -= (needed.width - box.width) / 2;
        box.width = needed.width;
      }
      if (needed.height > box.height) {
        box.y -= (needed.height - box.height) / 2;
        box.height = needed.height;
      }
    }

    // One top and one height per row: aligned tops read as a row, and equal heights put
    // every centre on the same line, which is what keeps the arrows between them level.
    const top = Math.min(...row.map((b) => b.y));
    const height = Math.max(...row.map((b) => b.height));
    for (const box of row) {
      box.y = top;
      box.height = height;
    }

    // Left to right, each box clears the one before it.
    let edge = -Infinity;
    for (const box of [...row].sort((a, b) => a.x - b.x)) {
      if (box.x < edge) box.x = edge;
      edge = box.x + box.width + MIN_GAP;
    }
  }

  // Top to bottom, each row clears the one above it.
  let floor = -Infinity;
  for (const row of rows) {
    const top = row[0].y;
    if (top < floor) {
      const shift = floor - top;
      for (const box of row) box.y += shift;
    }
    floor = row[0].y + row[0].height + MIN_GAP;
  }

  // Finally, wrap each boundary back around whatever it was drawn around.
  for (const boundary of boundaries) {
    const children = enclosed.get(boundary) ?? [];
    if (children.length === 0) continue;

    const left = Math.min(...children.map((c) => c.x)) - CONTAINER_PADDING;
    const top = Math.min(...children.map((c) => c.y)) - CONTAINER_PADDING;
    boundary.x = left;
    boundary.y = top;
    boundary.width = Math.max(...children.map((c) => c.x + c.width)) + CONTAINER_PADDING - left;
    boundary.height = Math.max(...children.map((c) => c.y + c.height)) + CONTAINER_PADDING - top;
  }
}
