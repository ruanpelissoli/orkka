import { useSyncExternalStore } from "react";
import { createPortal } from "react-dom";
import { cn } from "@/lib/utils";

/**
 * One column owns the bottom-right corner. It clears the mobile tab bar on small screens;
 * pointer events are handed back per slot so the empty column never eats clicks on the
 * page behind it.
 */
const CONTAINER_CLASS =
  "pointer-events-none fixed right-4 bottom-[calc(4.5rem+env(safe-area-inset-bottom))] z-40 flex flex-col-reverse items-end gap-3 lg:right-6 lg:bottom-6";

/**
 * Stack positions, counted outwards from the screen corner. Shell-level launchers keep the
 * corner on every page and page-scoped ones stack above them, so opening a page that adds a
 * launcher never moves the buttons that are always there.
 */
const FLOATING_DOCK_ORDER = {
  terminal: 10,
  run: 20,
  liveApp: 30,
  agentChat: 40,
} as const;

type FloatingDockOrder = (typeof FLOATING_DOCK_ORDER)[keyof typeof FLOATING_DOCK_ORDER];

// The mounted host, published to the slots as an external store: a slot may render before
// (tests) or after (page navigation) the host exists, and either way it has to react.
let hostElement: HTMLElement | null = null;
const listeners = new Set<() => void>();

function registerHost(element: HTMLElement | null) {
  if (hostElement === element) return;
  hostElement = element;
  for (const listener of listeners) listener();
}

function subscribeHost(listener: () => void) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

function getHost() {
  return hostElement;
}

// Server render: no host yet, so slots fall back to owning the corner themselves.
function getServerHost() {
  return null;
}

/**
 * Mount once per shell. Every floating launcher (run, terminal, agent chat) renders into
 * this column instead of pinning itself to the corner — which is what used to make two
 * launchers land on top of each other.
 */
function FloatingDockHost() {
  return <div ref={registerHost} className={CONTAINER_CLASS} />;
}

interface FloatingDockSlotProps {
  /** Distance from the corner — use a FLOATING_DOCK_ORDER key. */
  order: FloatingDockOrder;
  className?: string;
  children: React.ReactNode;
}

/**
 * Places a launcher in the shell's dock column. Falls back to owning the corner itself when
 * no host is mounted (standalone renders, tests), so a launcher is never invisible.
 */
function FloatingDockSlot({ order, className, children }: FloatingDockSlotProps) {
  const host = useSyncExternalStore(subscribeHost, getHost, getServerHost);

  const item = (
    <div style={{ order }} className={cn("pointer-events-auto", className)}>
      {children}
    </div>
  );

  if (host) return createPortal(item, host);

  return <div className={CONTAINER_CLASS}>{item}</div>;
}

export { FloatingDockHost, FloatingDockSlot, FLOATING_DOCK_ORDER };
export type { FloatingDockOrder };
