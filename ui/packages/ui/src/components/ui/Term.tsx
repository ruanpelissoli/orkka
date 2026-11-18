import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from "react";
import { useTranslations } from "use-intl";
import { motion, AnimatePresence } from "framer-motion";
import { cn } from "@/lib/utils";
import { useUserPreferencesContext } from "@/components/providers/UserPreferencesProvider";
import type { GlossaryKey } from "./glossary";

interface TermProps {
  /** Glossary key — see `glossary.ts` and `common.json` → `glossary`. */
  k: GlossaryKey;
  /** The words as they appear in the sentence. Defaults to the glossary term itself. */
  children?: ReactNode;
  className?: string;
  /**
   * Render the plain words with no underline or popover. Used when the member has
   * switched on "Show technical details" — developers do not need "repository" explained
   * — and for places where the hover would fight another interaction.
   */
  plain?: boolean;
}

const VIEWPORT_PAD = 8;

/**
 * A piece of jargon with a plain-language explanation one hover (or tap) away.
 *
 * Renders its children with a dotted underline; hovering, focusing or tapping opens a
 * small card with the glossary term and definition. Keyboard: Tab to focus opens it,
 * Escape closes. Touch: tap toggles, tapping anywhere else closes.
 *
 * Never nest it inside a button or link — it is focusable itself. Wrap the heading or
 * label next to the control instead.
 */
function Term({ k, children, className, plain }: TermProps) {
  const t = useTranslations("common.glossary");
  const { showTechnicalDetails } = useUserPreferencesContext();
  const [open, setOpen] = useState(false);
  const [placement, setPlacement] = useState<"top" | "bottom">("top");
  const [shift, setShift] = useState(0);
  const rootRef = useRef<HTMLSpanElement>(null);
  const cardRef = useRef<HTMLSpanElement>(null);
  const id = useId();

  const close = useCallback(() => setOpen(false), []);

  // Keep the card on screen: nudge it sideways when it would clip a viewport edge and
  // flip below the term when there is no room above.
  useLayoutEffect(() => {
    if (!open || !cardRef.current || !rootRef.current) return;
    const card = cardRef.current.getBoundingClientRect();
    const anchor = rootRef.current.getBoundingClientRect();
    const unshifted = { left: card.left - shift, right: card.right - shift };
    let next = 0;
    if (unshifted.left < VIEWPORT_PAD) next = VIEWPORT_PAD - unshifted.left;
    else if (unshifted.right > window.innerWidth - VIEWPORT_PAD)
      next = window.innerWidth - VIEWPORT_PAD - unshifted.right;
    if (next !== shift) setShift(next);
    const wanted = anchor.top - card.height - VIEWPORT_PAD < 0 ? "bottom" : "top";
    if (wanted !== placement) setPlacement(wanted);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- measure once per open
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: PointerEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) close();
    };
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open, close]);

  const label = children ?? t(`${k}.term`);

  if (plain || showTechnicalDetails) {
    return <>{label}</>;
  }

  const onKeyDown = (e: KeyboardEvent<HTMLSpanElement>) => {
    if (e.key === "Escape") {
      e.stopPropagation();
      close();
    } else if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      setOpen((o) => !o);
    }
  };

  return (
    <span ref={rootRef} className="relative inline">
      <span
        tabIndex={0}
        aria-describedby={open ? id : undefined}
        data-glossary-term={k}
        className={cn(
          "cursor-help rounded-sm underline decoration-dotted decoration-gray-500 underline-offset-4",
          "transition-colors hover:decoration-gray-300 focus-visible:decoration-gray-300",
          "focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary-500/60",
          className
        )}
        onMouseEnter={() => setOpen(true)}
        onMouseLeave={close}
        onFocus={() => setOpen(true)}
        onBlur={close}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={onKeyDown}
      >
        {label}
      </span>
      <AnimatePresence>
        {open && (
          <motion.span
            ref={cardRef}
            role="tooltip"
            id={id}
            initial={{ opacity: 0, scale: 0.97, y: placement === "top" ? 4 : -4 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={{ opacity: 0, scale: 0.97 }}
            transition={{ duration: 0.12 }}
            style={{ left: `calc(50% + ${shift}px)` }}
            className={cn(
              "absolute z-50 block w-64 -translate-x-1/2 rounded-xl p-3 text-left normal-case tracking-normal",
              "border border-white/[0.08] bg-gray-900/95 backdrop-blur-lg",
              "shadow-[0_8px_30px_rgba(0,0,0,0.45)]",
              "pointer-events-none select-none",
              placement === "top" ? "bottom-full mb-2" : "top-full mt-2"
            )}
          >
            <span className="block text-sm font-semibold leading-snug text-white">
              {t(`${k}.term`)}
            </span>
            <span className="mt-1 block text-xs font-normal leading-relaxed text-gray-400">
              {t(`${k}.definition`)}
            </span>
          </motion.span>
        )}
      </AnimatePresence>
    </span>
  );
}

export { Term };
export type { TermProps };
