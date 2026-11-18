import { useEffect, useRef, useState } from "react";
import { animate, useReducedMotion } from "framer-motion";

/**
 * A number that counts to its new value instead of snapping to it.
 *
 * Used where a figure changes underneath the viewer while they are looking at
 * it — spend during a task, token counts on an
 * agent card. A number replaced in place is easy to miss entirely; one that
 * moves is not, and the direction of travel is legible without reading it.
 *
 * Only worth it for live figures. A static price or an invoice total should be
 * plain text — animating those is decoration, and slower to read.
 */
export function AnimatedNumber({
  value,
  format = (n) => n.toLocaleString(),
  className,
  /** Skip the animation on first paint. On by default: a dashboard should not
   *  count up from zero every time it loads. */
  animateOnMount = false,
  durationMs = 420,
}: {
  value: number;
  format?: (n: number) => string;
  className?: string;
  animateOnMount?: boolean;
  durationMs?: number;
}) {
  const [display, setDisplay] = useState(value);
  const previous = useRef(value);
  const mounted = useRef(false);
  const reduceMotion = useReducedMotion();

  useEffect(() => {
    const from = previous.current;
    previous.current = value;

    const skip = (!mounted.current && !animateOnMount) || reduceMotion;
    mounted.current = true;
    if (skip || from === value) {
      setDisplay(value);
      return;
    }

    // framer's `animate` on a plain value — no DOM node involved, so this works
    // for a figure rendered inside arbitrary markup.
    const controls = animate(from, value, {
      duration: durationMs / 1000,
      ease: [0.16, 1, 0.3, 1],
      onUpdate: setDisplay,
    });
    return () => controls.stop();
  }, [value, animateOnMount, durationMs, reduceMotion]);

  // tabular-nums stops the text width jittering as digits change, which
  // otherwise makes the whole row twitch during the count.
  return <span className={className} style={{ fontVariantNumeric: "tabular-nums" }}>{format(display)}</span>;
}
