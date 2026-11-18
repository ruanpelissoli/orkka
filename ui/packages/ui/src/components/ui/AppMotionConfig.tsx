import { MotionConfig } from "framer-motion";

/**
 * Makes every Framer Motion animation in the app respect the OS "reduce motion"
 * setting.
 *
 * This is not redundant with the `prefers-reduced-motion` block in theme.css.
 * That block sets `animation-duration` and `transition-duration` to ~0, which
 * covers CSS keyframes and CSS transitions — the orca animations, the shimmer,
 * hover colour changes. Framer Motion does not use either: it drives inline
 * style values frame by frame from JS, so a CSS duration override has nothing
 * to override. Before this component the app had 17 files animating with Framer
 * Motion, every one of them ignoring the user's setting.
 *
 * `reducedMotion="user"` defers to the media query rather than forcing a
 * choice. Under it, Framer Motion keeps opacity and colour transitions (which
 * do not cause vestibular problems) and drops transform and layout animation —
 * so a task still fades between lanes, it just doesn't fly.
 */
export function AppMotionConfig({ children }: { children: React.ReactNode }) {
  return <MotionConfig reducedMotion="user">{children}</MotionConfig>;
}
