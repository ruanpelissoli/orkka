/**
 * Where the tour card goes relative to the element it is explaining.
 *
 * Split out from the overlay so the geometry is testable without a DOM: the failure mode this
 * guards against is a card that reads fine at 1440px and hangs off the edge at 1024px, which is
 * invisible in review and obvious to the user.
 */

interface AnchorRect {
  top: number;
  left: number;
  width: number;
  height: number;
}

interface Viewport {
  width: number;
  height: number;
}

interface CardSize {
  width: number;
  height: number;
}

interface Placement {
  top: number;
  left: number;
  /** Which edge of the card the pointer sits on, or null when the card is centered. */
  arrow: "left" | "top" | null;
}

const GAP = 16;

function clamp(value: number, min: number, max: number): number {
  // max < min when the card is taller than the viewport — pin to the top rather than
  // returning a negative value from a backwards clamp.
  if (max < min) return min;
  return Math.min(Math.max(value, min), max);
}

/**
 * Prefers the right of the anchor (the sidebar sits on the left, so this reads as a callout
 * pointing back at the nav item), falls back to below it, and centers when neither fits —
 * which is also the no-anchor case on mobile, where the sidebar is inside a closed drawer.
 */
function placeCard(
  anchor: AnchorRect | null,
  card: CardSize,
  viewport: Viewport
): Placement {
  const centered: Placement = {
    top: clamp(
      (viewport.height - card.height) / 2,
      GAP,
      viewport.height - card.height - GAP
    ),
    left: clamp(
      (viewport.width - card.width) / 2,
      GAP,
      viewport.width - card.width - GAP
    ),
    arrow: null,
  };

  if (!anchor) return centered;

  const rightEdge = anchor.left + anchor.width + GAP;
  if (rightEdge + card.width <= viewport.width - GAP) {
    return {
      top: clamp(anchor.top - 8, GAP, viewport.height - card.height - GAP),
      left: rightEdge,
      arrow: "left",
    };
  }

  const belowEdge = anchor.top + anchor.height + GAP;
  if (belowEdge + card.height <= viewport.height - GAP) {
    return {
      top: belowEdge,
      left: clamp(anchor.left, GAP, viewport.width - card.width - GAP),
      arrow: "top",
    };
  }

  return centered;
}

export { placeCard, GAP as TOUR_GAP };
export type { AnchorRect, CardSize, Placement, Viewport };
