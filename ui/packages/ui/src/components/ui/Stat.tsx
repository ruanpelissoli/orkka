import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

type StatTone = "neutral" | "good" | "warn" | "bad";
type StatSize = "sm" | "md" | "lg";

const VALUE_SIZE: Record<StatSize, string> = {
  // In-card 3-ups (agent health, run drawer) — sits beside body copy, so it stays at text size.
  sm: "text-xs",
  // Strip cells.
  md: "text-2xl",
  // A page's headline figure, e.g. a usage total.
  lg: "text-3xl",
};

const TONE_CLASS: Record<StatTone, string> = {
  neutral: "text-white",
  good: "text-emerald-400",
  warn: "text-amber-400",
  bad: "text-red-400",
};

interface StatProps {
  label: string;
  /** Pre-formatted. A Stat renders numbers, it does not decide how they read. */
  value: ReactNode;
  /** The denominator, the comparison, the caveat — whatever keeps the value honest. */
  hint?: ReactNode;
  /** Trend pill, alert badge, anything that qualifies the value at a glance. */
  badge?: ReactNode;
  /**
   * Glowing dot before the label, the same marker lane headers use. Deliberately a dot rather
   * than an icon: the app's icon set is a closed nav vocabulary with no money, clock or alert
   * glyph, and a nav icon pressed into meaning "spend" reads worse than no icon at all.
   */
  accentColor?: string;
  tone?: StatTone;
  size?: StatSize;
  className?: string;
}

/**
 * One metric: a label, a number, and whatever keeps that number honest.
 *
 * The label is always `.text-label` — the design system's uppercase micro-label — and the value
 * is always the display face. Every metric surface in the app had rebuilt this pair by hand with
 * slightly different sizes; this is the shape they now share.
 */
function Stat({
  label,
  value,
  hint,
  badge,
  accentColor,
  tone = "neutral",
  size = "md",
  className,
}: StatProps) {
  return (
    <div className={cn("min-w-0", className)}>
      <p className="flex items-center gap-1.5 text-label">
        {accentColor && (
          <span
            className="h-2 w-2 shrink-0 rounded-full"
            style={{ backgroundColor: accentColor, boxShadow: `0 0 8px ${accentColor}50` }}
          />
        )}
        {label}
      </p>
      <div className="mt-1 flex items-center gap-2">
        <p
          className={cn(
            "font-display font-bold tabular-nums",
            VALUE_SIZE[size],
            TONE_CLASS[tone]
          )}
        >
          {value}
        </p>
        {badge}
      </div>
      {hint && <p className="mt-0.5 truncate text-xs text-gray-400">{hint}</p>}
    </div>
  );
}

/**
 * A row of Stats split by hairlines — the KPI strip shape. Collapses to two columns on narrow
 * screens rather than scrolling sideways, because a metric you have to scroll to is a metric
 * nobody reads.
 */
function StatStrip({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div
      className={cn(
        "grid grid-cols-2 md:grid-cols-4",
        // Hairlines are borders on the cells rather than a gap over a tinted parent, because the
        // parent here is glass: opaque cells would flatten the card they sit in. The nth-child
        // rules just drop the borders that would run along the card's own edge — first row has
        // no top, first column no left, at each breakpoint's column count.
        "[&>*]:border-l [&>*]:border-t [&>*]:border-white/[0.06]",
        "[&>*:nth-child(-n+2)]:border-t-0 [&>*:nth-child(odd)]:border-l-0",
        "md:[&>*]:border-t-0 md:[&>*:nth-child(odd)]:border-l md:[&>*:first-child]:border-l-0",
        className
      )}
    >
      {children}
    </div>
  );
}

/** One cell of a {@link StatStrip}. Owns only its padding; the divider is the strip's business. */
function StatStripCell({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("p-4", className)}>{children}</div>;
}

/**
 * The app's shared threshold-to-tone mapping for success rates, so the agent card, the spend
 * panel and the board strip never disagree about what counts as healthy.
 */
function toneForSuccessRate(pct: number | null | undefined): StatTone {
  if (pct === null || pct === undefined || !Number.isFinite(pct)) return "neutral";
  if (pct >= 80) return "good";
  if (pct >= 50) return "warn";
  return "bad";
}

export { Stat, StatStrip, StatStripCell, toneForSuccessRate };
export type { StatProps, StatTone, StatSize };
