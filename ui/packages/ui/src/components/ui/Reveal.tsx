import { motion } from "framer-motion";
import { revealUp, stagger, VIEWPORT } from "@/lib/motion";

/**
 * Scroll-triggered reveal for landing-page content.
 *
 * Most of the marketing sections are server components — Hero, Pricing and CTA
 * are `async` and call `getTranslations`. Framer Motion needs a client
 * component, so rather than convert those (and lose server-rendered copy), this
 * wraps their already-rendered children. `children` stays a server component;
 * only the wrapper ships to the browser.
 *
 * `once: true` is deliberate. A section that re-animates every time it scrolls
 * back into view is the fastest way to make a page feel like a demo reel.
 */
export function Reveal({
  children,
  delay = 0,
  className,
  as = "div",
}: {
  children: React.ReactNode;
  /** Seconds. For sequencing two or three sibling blocks by hand. */
  delay?: number;
  className?: string;
  as?: "div" | "section" | "li";
}) {
  const Component = motion[as];
  return (
    <Component
      className={className}
      variants={revealUp}
      initial="hidden"
      whileInView="visible"
      viewport={VIEWPORT}
      transition={delay ? { delay } : undefined}
    >
      {children}
    </Component>
  );
}

/**
 * A `Reveal` whose direct children come in one after another.
 *
 * Children must be `RevealItem` (or any motion element using the same variant
 * names) for the stagger to reach them — Framer propagates variant state down
 * the tree by name, not by wrapping.
 */
export function RevealGroup({
  children,
  className,
  staggerChildren = 0.07,
  delayChildren = 0,
}: {
  children: React.ReactNode;
  className?: string;
  staggerChildren?: number;
  delayChildren?: number;
}) {
  return (
    <motion.div
      className={className}
      variants={stagger(staggerChildren, delayChildren)}
      initial="hidden"
      whileInView="visible"
      viewport={VIEWPORT}
    >
      {children}
    </motion.div>
  );
}

/** A child of `RevealGroup`. Inherits the parent's staggered timing. */
export function RevealItem({
  children,
  className,
}: {
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <motion.div className={className} variants={revealUp}>
      {children}
    </motion.div>
  );
}
