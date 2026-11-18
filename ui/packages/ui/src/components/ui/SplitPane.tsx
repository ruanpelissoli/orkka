import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";

interface SplitPaneProps {
  /** Left (or top) pane. */
  first: ReactNode;
  /** Right (or bottom) pane — takes whatever the first pane leaves. */
  second: ReactNode;
  /** Initial size of the first pane in px; overridden by the persisted value when `storageKey` is set. */
  defaultSize?: number;
  minSize?: number;
  maxSize?: number;
  /** localStorage key to remember the user's drag. Omit for a non-persistent split. */
  storageKey?: string;
  className?: string;
  /** Accessible name for the divider. */
  dividerLabel?: string;
}

function readStored(key: string | undefined, fallback: number): number {
  if (!key || typeof window === "undefined") return fallback;
  const raw = window.localStorage.getItem(key);
  const n = raw ? Number(raw) : NaN;
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

/**
 * Two panes with a draggable vertical divider. Deliberately small: pointer events + a
 * remembered width. Keyboard: arrows nudge 16px, Home/End snap to min/max.
 */
function SplitPane({
  first,
  second,
  defaultSize = 380,
  minSize = 280,
  maxSize = 720,
  storageKey,
  className,
  dividerLabel = "Resize panels",
}: SplitPaneProps) {
  const [size, setSize] = useState(() => readStored(storageKey, defaultSize));
  const [dragging, setDragging] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const clamp = useCallback(
    (n: number) => Math.min(maxSize, Math.max(minSize, Math.round(n))),
    [minSize, maxSize]
  );

  useEffect(() => {
    if (!storageKey) return;
    window.localStorage.setItem(storageKey, String(size));
  }, [size, storageKey]);

  const onPointerDown = useCallback((e: React.PointerEvent<HTMLDivElement>) => {
    e.preventDefault();
    (e.currentTarget as HTMLDivElement).setPointerCapture(e.pointerId);
    setDragging(true);
  }, []);

  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      if (!dragging || !containerRef.current) return;
      const rect = containerRef.current.getBoundingClientRect();
      setSize(clamp(e.clientX - rect.left));
    },
    [dragging, clamp]
  );

  const stopDragging = useCallback(() => setDragging(false), []);

  const onKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLDivElement>) => {
      const step = e.shiftKey ? 64 : 16;
      if (e.key === "ArrowLeft") setSize((s) => clamp(s - step));
      else if (e.key === "ArrowRight") setSize((s) => clamp(s + step));
      else if (e.key === "Home") setSize(minSize);
      else if (e.key === "End") setSize(maxSize);
      else return;
      e.preventDefault();
    },
    [clamp, minSize, maxSize]
  );

  return (
    <div ref={containerRef} className={cn("flex min-h-0 min-w-0 flex-1", dragging && "select-none", className)}>
      <div className="flex min-h-0 shrink-0 flex-col" style={{ width: size }}>
        {first}
      </div>
      <div
        role="separator"
        aria-orientation="vertical"
        aria-label={dividerLabel}
        aria-valuenow={size}
        aria-valuemin={minSize}
        aria-valuemax={maxSize}
        tabIndex={0}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={stopDragging}
        onPointerCancel={stopDragging}
        onKeyDown={onKeyDown}
        className={cn(
          "group relative z-10 w-1.5 shrink-0 cursor-col-resize touch-none outline-none",
          "before:absolute before:inset-y-0 before:left-1/2 before:w-px before:-translate-x-1/2 before:bg-white/[0.08] before:transition-colors",
          "hover:before:bg-primary-400/60 focus-visible:before:bg-primary-400",
          dragging && "before:bg-primary-400"
        )}
      />
      <div className="flex min-h-0 min-w-0 flex-1 flex-col">{second}</div>
    </div>
  );
}

export { SplitPane };
export type { SplitPaneProps };
