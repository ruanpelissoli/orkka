import type { Transition, Variants } from "framer-motion";

/**
 * The app's motion vocabulary — durations, easings and the handful of variants
 * every animated surface reuses.
 *
 * The brief is "subtle and fast": motion exists to explain what changed, then
 * get out of the way. Concretely that means 150–300ms, small offsets (4–12px),
 * and ease-out so things decelerate into place rather than bouncing. A board you
 * look at all day cannot afford springy 500ms transitions on every card.
 *
 * Import these rather than writing literals. The reason is the same one behind
 * the typography and colour tokens: fifteen components each picking their own
 * "0.25, easeInOut" is how an interface ends up feeling subtly incoherent, and
 * it is invisible in review because every individual number looks reasonable.
 *
 * Reduced motion is NOT handled here. It is handled once, at the root, by
 * <AppMotionConfig> — see that component for why the CSS media query in
 * theme.css was not enough.
 */

// ── Durations (seconds, framer-motion's unit) ───────────────────────────

export const DURATION = {
  /** Colour and opacity changes on hover/press. Should feel instant. */
  instant: 0.12,
  /** The default. Enter/exit, expand/collapse, most state changes. */
  fast: 0.18,
  /** Something moving a meaningful distance, or a larger surface. */
  medium: 0.24,
  /** Reserved for the landing page, where a reveal is the point. */
  slow: 0.32,
} as const;

// ── Easings ─────────────────────────────────────────────────────────────

/**
 * Standard ease-out. Fast at the start, settling at the end — the curve that
 * reads as "responsive" because the first half of the motion happens almost
 * immediately.
 */
export const EASE_OUT = [0.16, 1, 0.3, 1] as const;

/** Symmetric, for things that move and come back (accordions, toggles). */
export const EASE_IN_OUT = [0.65, 0, 0.35, 1] as const;

/**
 * The one spring in the system, for layout changes where a card physically
 * travels — a task moving lane. Damped hard enough not to overshoot visibly;
 * it exists to make the path feel weighted, not bouncy.
 */
export const SPRING_LAYOUT: Transition = {
  type: "spring",
  stiffness: 520,
  damping: 42,
  mass: 0.9,
};

// ── Transitions ─────────────────────────────────────────────────────────

export const transition = {
  instant: { duration: DURATION.instant, ease: EASE_OUT },
  fast: { duration: DURATION.fast, ease: EASE_OUT },
  medium: { duration: DURATION.medium, ease: EASE_OUT },
  slow: { duration: DURATION.slow, ease: EASE_OUT },
  inOut: { duration: DURATION.medium, ease: EASE_IN_OUT },
} satisfies Record<string, Transition>;

// ── Variants ────────────────────────────────────────────────────────────

/** Fade only. For things that shouldn't appear to move — overlays, crossfades. */
export const fade: Variants = {
  hidden: { opacity: 0 },
  visible: { opacity: 1, transition: transition.fast },
  exit: { opacity: 0, transition: transition.instant },
};

/** The workhorse: fade with a small rise. Cards, rows, messages, panels. */
export const fadeUp: Variants = {
  hidden: { opacity: 0, y: 8 },
  visible: { opacity: 1, y: 0, transition: transition.medium },
  exit: { opacity: 0, y: -4, transition: transition.fast },
};

/** Same, entering from below — for content appended to the bottom of a list. */
export const fadeUpFromBelow: Variants = {
  hidden: { opacity: 0, y: 12 },
  visible: { opacity: 1, y: 0, transition: transition.medium },
  exit: { opacity: 0, transition: transition.instant },
};

/** For an item arriving in or leaving a list, where collapse matters. */
export const listItem: Variants = {
  hidden: { opacity: 0, y: 8, scale: 0.98 },
  visible: { opacity: 1, y: 0, scale: 1, transition: transition.medium },
  exit: { opacity: 0, scale: 0.97, transition: transition.fast },
};

/**
 * Height collapse, for anything that expands in place — accordions, diff hunks,
 * a banner appearing inside a card already on screen.
 *
 * Put this on a wrapper with `overflow-hidden` and keep padding/margin on a
 * child. Framer measures the child to resolve `height: "auto"`, so spacing on
 * the child is included in the measurement and the content below settles once,
 * rather than jumping when the animation ends.
 */
export const collapse: Variants = {
  hidden: { height: 0, opacity: 0, transition: transition.fast },
  visible: { height: "auto", opacity: 1, transition: transition.medium },
};

/**
 * A parent that staggers its children in. Pair with a child using `fadeUp`.
 * 0.05s is deliberately tight — enough to read as a sequence, not a queue.
 */
export const stagger = (staggerChildren = 0.05, delayChildren = 0): Variants => ({
  hidden: {},
  visible: { transition: { staggerChildren, delayChildren } },
});

/** Section reveal on the landing page, where a slower, longer travel is wanted. */
export const revealUp: Variants = {
  hidden: { opacity: 0, y: 20 },
  visible: { opacity: 1, y: 0, transition: transition.slow },
};

/**
 * Standard viewport config for scroll-triggered reveals. `once` matters: a
 * section that re-animates every time it scrolls back into view is the fastest
 * way to make a page feel gimmicky.
 */
export const VIEWPORT = { once: true, margin: "-80px" } as const;
