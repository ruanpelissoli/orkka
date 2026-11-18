import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { AnimatePresence, motion } from "framer-motion";
import { useTranslations } from "use-intl";
import { usePathname, useRouter } from "@/i18n/navigation";
import { useProject } from "@/hooks/use-project";
import { useUserPreferencesContext } from "@/components/providers/UserPreferencesProvider";
import { cn } from "@/lib/utils";
import { getTourSteps, TOUR_ANCHOR_ATTR } from "./tour-steps";
import { placeCard, type AnchorRect } from "./tour-placement";

/** Survives a hard reload mid-tour. Session-scoped on purpose: the durable "did they see it"
 *  answer is the member preference, this only remembers *where* they were. */
const STEP_STORAGE_KEY = "orkka.tour.step";

const CARD_WIDTH = 340;
/** Used until the card has been measured. */
const CARD_HEIGHT_ESTIMATE = 210;
/** ~1.5s of re-measuring after each step, so the spotlight settles as the route loads. */
const SETTLE_FRAMES = 90;
/** gray-950 (#0b0f18), the app background — the dim has to read as the page going dark. */
const DIM = "rgba(11, 15, 24, 0.82)";

/** The tour, once it has started: which project it is walking and how far along it is. */
interface TourSession {
  slug: string;
  index: number;
}

function readSavedStep(): number {
  if (typeof window === "undefined") return 0;
  try {
    const raw = window.sessionStorage.getItem(STEP_STORAGE_KEY);
    const parsed = raw === null ? 0 : Number.parseInt(raw, 10);
    return Number.isInteger(parsed) && parsed > 0 ? parsed : 0;
  } catch {
    // Private browsing / storage disabled — start from the beginning.
    return 0;
  }
}

function writeSavedStep(index: number | null) {
  try {
    if (index === null) window.sessionStorage.removeItem(STEP_STORAGE_KEY);
    else window.sessionStorage.setItem(STEP_STORAGE_KEY, String(index));
  } catch {
    // Not worth failing the tour over.
  }
}

function readViewport() {
  if (typeof window === "undefined") return { width: 0, height: 0 };
  return { width: window.innerWidth, height: window.innerHeight };
}

function sameRect(a: AnchorRect | null, b: AnchorRect | null): boolean {
  if (a === null || b === null) return a === b;
  return (
    a.top === b.top &&
    a.left === b.left &&
    a.width === b.width &&
    a.height === b.height
  );
}

/**
 * Tracks the on-screen box of the current step's anchor.
 *
 * Re-measures for a short while after every step change rather than measuring once: the step
 * navigates, and the sidebar item it points at can shift while the new route paints (a scrollbar
 * appearing, the project-status chip resolving). Measuring once catches the pre-paint position
 * and leaves the spotlight a few pixels off.
 */
function useAnchorRect(anchor: string | undefined, stepIndex: number): AnchorRect | null {
  const [rect, setRect] = useState<AnchorRect | null>(null);

  // A step without an anchor drops the spotlight.
  const [prevAnchor, setPrevAnchor] = useState(anchor);
  if (anchor !== prevAnchor) {
    setPrevAnchor(anchor);
    if (!anchor) setRect(null);
  }

  useEffect(() => {
    if (!anchor) return;

    const selector = `[${TOUR_ANCHOR_ATTR}="${anchor}"]`;
    let frame = 0;
    let frames = 0;
    let cancelled = false;

    const measure = () => {
      if (cancelled) return;
      const box = document.querySelector(selector)?.getBoundingClientRect() ?? null;
      const next: AnchorRect | null = box
        ? { top: box.top, left: box.left, width: box.width, height: box.height }
        : null;
      setRect((prev) => (sameRect(prev, next) ? prev : next));
      if (frames++ < SETTLE_FRAMES) frame = requestAnimationFrame(measure);
    };

    // The nav column scrolls independently of the page, so a step near the bottom of a long
    // sidebar has to be brought into view before it can be spotlighted at all.
    document.querySelector(selector)?.scrollIntoView({ block: "nearest" });

    measure();
    window.addEventListener("resize", measure);
    return () => {
      cancelled = true;
      cancelAnimationFrame(frame);
      window.removeEventListener("resize", measure);
    };
  }, [anchor, stepIndex]);

  return rect;
}

/**
 * One-time guided tour of every page, shown once the member has a project open.
 *
 * Mounted in the dashboard shell so it outlives the route changes it drives — each step pushes
 * to the page it explains, and losing the step index on navigation would restart the tour on
 * every Next. Finishing or skipping writes `onboardingTourSeen` to the member's preferences,
 * which is what stops it coming back on another device.
 */
function ProductTour() {
  const t = useTranslations("dashboard");
  const router = useRouter();
  const pathname = usePathname();
  const { currentProject } = useProject();
  const { onboardingTourSeen, setOnboardingTourSeen } = useUserPreferencesContext();

  const [session, setSession] = useState<TourSession | null>(null);
  const [cardHeight, setCardHeight] = useState(CARD_HEIGHT_ESTIMATE);
  const [viewport, setViewport] = useState(readViewport);
  const observerRef = useRef<ResizeObserver | null>(null);
  /** Ends the tour for this mount even if the preference write never lands. Without it, a
   *  ProductTour rendered outside UserPreferencesProvider — where the context setter is a
   *  no-op — would re-arm itself on the very next render after Finish. */
  const [ended, setEnded] = useState(false);
  const [lastSeen, setLastSeen] = useState(onboardingTourSeen);

  // Replaying from Preferences clears the flag; that is the one signal that re-arms the tour
  // after it has been ended once.
  if (lastSeen !== onboardingTourSeen) {
    setLastSeen(onboardingTourSeen);
    if (!onboardingTourSeen) setEnded(false);
  }

  /**
   * Start during render rather than from an effect — React's documented way to derive state
   * from changing inputs, and it avoids a frame where the tour is armed but not drawn.
   *
   * The session latches the project slug because later steps visit cross-project pages (Inbox,
   * Notifications, Account) where `currentProject` is null: a tour derived from the live project
   * would end itself three steps early.
   */
  if (!session && !ended && !onboardingTourSeen && currentProject) {
    setSession({ slug: currentProject.slug, index: readSavedStep() });
  }

  const steps = useMemo(() => (session ? getTourSteps(session.slug) : []), [session]);
  // A step index left over in sessionStorage from an older, longer tour must not read past
  // the end of the current one.
  const index = session ? Math.min(session.index, steps.length - 1) : 0;
  const step = steps[index];
  const isLast = index === steps.length - 1;

  useEffect(() => {
    if (session) writeSavedStep(index);
  }, [session, index]);

  // Walk to the page this step is about. Comparing against the live pathname keeps this to one
  // push per step — the effect re-runs when the navigation lands and then finds nothing to do.
  useEffect(() => {
    if (!step?.href) return;
    if (pathname !== step.href) router.push(step.href);
  }, [step, pathname, router]);

  useEffect(() => {
    if (!session) return;
    const read = () => setViewport(readViewport());
    window.addEventListener("resize", read);
    return () => window.removeEventListener("resize", read);
  }, [session]);

  // The page behind the overlay is not interactive during the tour, so letting it scroll
  // would only move content the user cannot reach out from under the spotlight.
  useEffect(() => {
    if (!session) return;
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = previous;
    };
  }, [session]);

  /** Measures the card so the placement math can keep it inside the viewport. A callback ref
   *  plus an observer, not a layout effect: the height changes with the copy of each step and
   *  with the width of the window, and the observer reports both. */
  const cardRef = useCallback((node: HTMLDivElement | null) => {
    observerRef.current?.disconnect();
    observerRef.current = null;
    if (!node) return;
    setCardHeight(node.offsetHeight || CARD_HEIGHT_ESTIMATE);
    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(() =>
      setCardHeight(node.offsetHeight || CARD_HEIGHT_ESTIMATE)
    );
    observer.observe(node);
    observerRef.current = observer;
  }, []);

  const anchorRect = useAnchorRect(step?.anchor, index);

  const finish = useCallback(() => {
    setEnded(true);
    setSession(null);
    writeSavedStep(null);
    void setOnboardingTourSeen(true);
  }, [setOnboardingTourSeen]);

  const back = useCallback(() => {
    setSession((current) =>
      current ? { ...current, index: Math.max(0, current.index - 1) } : current
    );
  }, []);

  const handleNext = useCallback(() => {
    if (isLast) {
      finish();
      return;
    }
    setSession((current) =>
      current
        ? { ...current, index: Math.min(current.index + 1, steps.length - 1) }
        : current
    );
  }, [isLast, finish, steps.length]);

  useEffect(() => {
    if (!session) return;
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        event.preventDefault();
        finish();
      } else if (event.key === "ArrowRight") {
        event.preventDefault();
        handleNext();
      } else if (event.key === "ArrowLeft") {
        event.preventDefault();
        back();
      }
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [session, finish, handleNext, back]);

  // No mounted flag: on the server there is nothing to portal into, and on the client the
  // session can only exist after ProjectProvider has fetched — which is after hydration.
  if (typeof document === "undefined" || !session || !step) return null;

  const placement = placeCard(
    anchorRect,
    { width: CARD_WIDTH, height: cardHeight },
    viewport
  );
  const progress = ((index + 1) / steps.length) * 100;

  return createPortal(
    <AnimatePresence>
      <div className="fixed inset-0 z-[100]" data-testid="product-tour">
        {/* Swallows every click aimed at the app underneath. Transparent when a spotlight is
            painting the dim; opaque on mobile, where there is no sidebar to point at. */}
        <div
          className={cn(
            "absolute inset-0",
            !anchorRect && "bg-gray-950/80 backdrop-blur-[2px]"
          )}
          onClick={(event) => event.stopPropagation()}
        />

        {anchorRect && (
          <motion.div
            aria-hidden
            initial={false}
            animate={{
              top: anchorRect.top - 4,
              left: anchorRect.left - 4,
              width: anchorRect.width + 8,
              height: anchorRect.height + 8,
            }}
            transition={{ type: "spring", stiffness: 500, damping: 40 }}
            className="pointer-events-none absolute rounded-xl ring-2 ring-primary-400/70"
            // A ring plus an enormous shadow spread is the whole spotlight: everything outside
            // the ring is dimmed by the shadow, and the hole needs no SVG mask.
            style={{ boxShadow: `0 0 0 9999px ${DIM}` }}
          />
        )}

        <motion.div
          ref={cardRef}
          role="dialog"
          aria-modal="true"
          aria-labelledby="product-tour-title"
          key={step.key}
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.18 }}
          className={cn(
            "absolute overflow-hidden rounded-2xl",
            "border border-white/[0.08] bg-gray-900/95 backdrop-blur-2xl",
            "shadow-[0_0_60px_rgba(0,0,0,0.5)]"
          )}
          style={{ top: placement.top, left: placement.left, width: CARD_WIDTH }}
        >
          <div className="absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-white/[0.1] to-transparent" />

          <div className="px-5 pt-4">
            <div className="flex items-center justify-between gap-3">
              <span className="text-label text-gray-400">
                {t("tour.progress", { current: index + 1, total: steps.length })}
              </span>
              <button
                type="button"
                onClick={finish}
                className="text-xs font-medium text-gray-400 transition-colors hover:text-gray-200"
              >
                {t("tour.skip")}
              </button>
            </div>
            <div className="mt-2 h-0.5 w-full overflow-hidden rounded-full bg-white/[0.06]">
              <motion.div
                className="h-full rounded-full bg-gradient-to-r from-primary-500 to-primary-400"
                initial={false}
                animate={{ width: `${progress}%` }}
                transition={{ duration: 0.25 }}
              />
            </div>
          </div>

          <div className="px-5 py-4">
            <h2
              id="product-tour-title"
              className="font-display text-base font-semibold text-white"
            >
              {t(`tour.steps.${step.key}.title`)}
            </h2>
            <p className="mt-1.5 text-sm leading-relaxed text-gray-400">
              {t(`tour.steps.${step.key}.body`)}
            </p>
          </div>

          <div className="flex items-center justify-end gap-2 border-t border-white/[0.06] px-5 py-3">
            {index > 0 && (
              <button
                type="button"
                onClick={back}
                className={cn(
                  "rounded-lg px-3 py-1.5 text-xs font-semibold text-gray-300",
                  "transition-colors hover:bg-white/[0.06] hover:text-white"
                )}
              >
                {t("tour.back")}
              </button>
            )}
            <button
              type="button"
              onClick={handleNext}
              autoFocus
              className={cn(
                "rounded-lg px-4 py-1.5 text-xs font-semibold text-white",
                "bg-gradient-to-r from-primary-600 to-primary-500",
                "shadow-[0_0_20px_rgba(59,130,246,0.15)] transition-all",
                "hover:from-primary-500 hover:to-primary-400"
              )}
            >
              {isLast ? t("tour.finish") : t("tour.next")}
            </button>
          </div>
        </motion.div>
      </div>
    </AnimatePresence>,
    document.body
  );
}

export { ProductTour, STEP_STORAGE_KEY };
