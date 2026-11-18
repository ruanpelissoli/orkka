import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslations } from "use-intl";
import { Button } from "@/components/ui";
import { cn } from "@/lib/utils";
import type { ProjectExecutionRow } from "@/lib/types";
import { bucketRuns } from "./activity-model";

interface ActivityDensityStripProps {
  rows: ProjectExecutionRow[];
  /** Window edges as epoch millis. */
  from: number;
  to: number;
  /** The active sub-range, or null for the whole window. */
  range: { from: number; to: number } | null;
  onRangeChange: (range: { from: number; to: number } | null) => void;
}

/** Buckets across the strip. 48 gives half-hour resolution on a 24h window. */
const BUCKET_COUNT = 48;

/** Drags shorter than this are treated as a click, and clear the selection. */
const DRAG_SLOP_PX = 4;

/**
 * The window's shape in one line: how many runs started when, with failures stacked in red.
 *
 * This is what is left of the old page's chart, and it is the part that was always worth keeping —
 * a burst at 2am or a dead afternoon is genuinely easier to see than to read. What it no longer
 * does is pretend to be the answer. Drag across it to narrow everything below to that slice.
 */
function ActivityDensityStrip({ rows, from, to, range, onRangeChange }: ActivityDensityStripProps) {
  const t = useTranslations("dashboard.activityPage");
  const trackRef = useRef<HTMLDivElement | null>(null);
  // Positions are kept as fractions of the track, measured in the pointer handlers where reading
  // the ref is legitimate. The render then only ever deals in percentages. `startX` rides along in
  // raw pixels purely so the click-versus-drag test below can be made in the unit it belongs in.
  const [drag, setDrag] = useState<{ startX: number; startRatio: number; endRatio: number } | null>(
    null
  );

  const buckets = useMemo(() => bucketRuns(rows, from, to, BUCKET_COUNT), [rows, from, to]);
  const peak = useMemo(() => Math.max(1, ...buckets.map((b) => b.runs)), [buckets]);

  const ratioAt = useCallback((clientX: number) => {
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0) return 0;
    return Math.min(Math.max((clientX - rect.left) / rect.width, 0), 1);
  }, []);

  const handlePointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    const ratio = ratioAt(e.clientX);
    setDrag({ startX: e.clientX, startRatio: ratio, endRatio: ratio });
  };

  const handlePointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    setDrag((d) => (d ? { ...d, endRatio: ratioAt(e.clientX) } : d));
  };

  const handlePointerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!drag) return;
    e.currentTarget.releasePointerCapture(e.pointerId);
    const travelled = Math.abs(e.clientX - drag.startX);
    const endRatio = ratioAt(e.clientX);
    setDrag(null);

    // A click is not a zero-width selection; it means "give me the whole window back".
    if (travelled < DRAG_SLOP_PX) {
      onRangeChange(null);
      return;
    }

    const span = to - from;
    const a = from + Math.min(drag.startRatio, endRatio) * span;
    const b = from + Math.max(drag.startRatio, endRatio) * span;
    onRangeChange({ from: a, to: b });
  };

  const selection = drag
    ? {
        left: Math.min(drag.startRatio, drag.endRatio) * 100,
        width: Math.abs(drag.endRatio - drag.startRatio) * 100,
      }
    : null;
  const committed = range ? toPercent(range, from, to) : null;

  return (
    <div className="space-y-1">
      <div
        ref={trackRef}
        className="relative flex h-10 cursor-crosshair items-end gap-px overflow-hidden rounded-lg border border-white/[0.06] bg-white/[0.02] px-1 pb-1 pt-2 select-none"
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        data-testid="density-strip"
      >
        {buckets.map((bucket) => {
          const height = bucket.runs === 0 ? 0 : Math.max((bucket.runs / peak) * 100, 8);
          const failedShare = bucket.runs === 0 ? 0 : (bucket.failed / bucket.runs) * 100;
          return (
            <div
              key={bucket.from}
              className="relative flex-1 self-end"
              style={{ height: `${height}%` }}
              title={`${bucket.runs}`}
              data-runs={bucket.runs}
            >
              <div className="absolute inset-0 rounded-[1px] bg-primary-500/50" />
              {bucket.failed > 0 && (
                <div
                  className="absolute inset-x-0 bottom-0 rounded-[1px] bg-red-500/70"
                  style={{ height: `${failedShare}%` }}
                />
              )}
            </div>
          );
        })}

        {committed && !selection && (
          <div
            className="pointer-events-none absolute inset-y-0 border-x border-primary-400/60 bg-primary-400/10"
            style={{ left: `${committed.left}%`, width: `${committed.width}%` }}
            data-testid="density-selection"
          />
        )}
        {selection && (
          <div
            className="pointer-events-none absolute inset-y-0 border-x border-primary-400/60 bg-primary-400/10"
            style={{ left: `${selection.left}%`, width: `${selection.width}%` }}
          />
        )}
      </div>

      <div className={cn("flex items-center gap-2 text-xs text-gray-400", !range && "invisible")}>
        <span>{t("rangeNarrowed")}</span>
        <Button size="sm" variant="ghost" onClick={() => onRangeChange(null)}>
          {t("rangeClear")}
        </Button>
      </div>
    </div>
  );
}

function toPercent(range: { from: number; to: number }, from: number, to: number) {
  const span = to - from || 1;
  const left = ((range.from - from) / span) * 100;
  const width = ((range.to - range.from) / span) * 100;
  return { left, width };
}

export { ActivityDensityStrip };
export type { ActivityDensityStripProps };
