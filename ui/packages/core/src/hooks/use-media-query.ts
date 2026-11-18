import { useSyncExternalStore } from "react";

/**
 * Reactive `window.matchMedia`. Returns `fallback` during SSR and the first client render
 * (before hydration can read the viewport), then the live match. Use it to choose between two
 * component trees — CSS `hidden` would mount both, and a tree that owns subscriptions or fetches
 * must exist exactly once.
 */
function useMediaQuery(query: string, fallback = false): boolean {
  return useSyncExternalStore(
    (onChange) => {
      if (typeof window === "undefined") return () => {};
      const mql = window.matchMedia(query);
      mql.addEventListener("change", onChange);
      return () => mql.removeEventListener("change", onChange);
    },
    () => (typeof window === "undefined" ? fallback : window.matchMedia(query).matches),
    () => fallback
  );
}

/** Tailwind's `lg` breakpoint and up. */
function useIsDesktop(): boolean {
  return useMediaQuery("(min-width: 1024px)", true);
}

export { useMediaQuery, useIsDesktop };
