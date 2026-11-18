import { useTranslations } from "use-intl";
import type { MilestonePlanStep } from "@/lib/types";

interface MilestonePlanProgressProps {
  step: MilestonePlanStep;
  /** Whole seconds since a worker claimed the run. */
  elapsedSeconds: number;
}

const STEP_KEY: Record<MilestonePlanStep, string> = {
  Queued: "queued",
  ReadingProject: "readingProject",
  Planning: "planning",
  Ordering: "ordering",
};

/** m:ss past a minute, plain seconds below it. A run measured in minutes should say so. */
function formatElapsed(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, "0")}`;
}

/**
 * What a planning run is doing, and for how long.
 *
 * This is not decoration. The incident that produced the asynchronous pipeline was a user
 * watching an unchanging spinner for 3m23s and then navigating away, which killed a generation
 * their org had already paid for. Nothing here can make the model faster — what it can do is
 * make the wait legible, and say out loud that leaving is safe.
 *
 * The phases are coarse because the truth is coarse: the model call is one opaque request, so
 * a percentage bar would be a lie. A named phase and a running clock are both real.
 */
export function MilestonePlanProgress({ step, elapsedSeconds }: MilestonePlanProgressProps) {
  const t = useTranslations("dashboard");

  return (
    <div
      className="space-y-2 rounded-lg border border-white/[0.06] bg-white/[0.02] p-4"
      role="status"
      aria-live="polite"
    >
      <div className="flex items-center gap-2.5">
        <span
          className="h-2 w-2 shrink-0 animate-pulse rounded-full bg-primary-400"
          aria-hidden="true"
        />
        <span className="text-sm text-white">
          {t(`roadmapPage.planProgress.${STEP_KEY[step]}`)}
        </span>
        <span className="ml-auto shrink-0 text-xs tabular-nums text-gray-400">
          {t("roadmapPage.planProgress.elapsed", { time: formatElapsed(elapsedSeconds) })}
        </span>
      </div>
      <p className="text-xs text-gray-400">{t("roadmapPage.planProgress.safeToLeave")}</p>
    </div>
  );
}
