import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";
import { LANE_MAP, type LaneKey } from "@/lib/constants";

/**
 * The orca at badge scale, animated to match what the agent is doing.
 *
 * The full-page `Orca*` states are 96–208px and carry a title and description —
 * right for an empty page, far too big for a board card. This is the same
 * mascot in the places a task actually lives: the card, the task header, the
 * run dock. Seeing the same creature dive while an agent codes and hang still
 * while it waits on you is what turns the logo into status language rather than
 * decoration.
 *
 * Motion honours prefers-reduced-motion through the global block in theme.css.
 */

export type OrcaState = "working" | "waiting" | "searching" | "done" | "idle";

/** Which lane means what the orca is doing. */
const LANE_STATE: Record<LaneKey, OrcaState> = {
  Drafting: "idle",
  Ready: "idle",
  Planning: "working",
  PlanReady: "waiting",
  PendingQuestions: "waiting",
  Developing: "working",
  Testing: "searching",
  CodeReview: "searching",
  Done: "done",
};

const ANIMATION: Record<OrcaState, string> = {
  // Diving: heads-down, making progress.
  working: "animate-[orca-dive_2.5s_ease-in-out_infinite]",
  // Bobbing at the surface, waiting to be told what to do.
  waiting: "animate-[orca-float_3s_ease-in-out_infinite]",
  // Casting about, looking for something.
  searching: "animate-[orca-search_2s_ease-in-out_infinite]",
  // A single settled breach.
  done: "",
  idle: "",
};

const SIZES = {
  xs: { box: "h-5 w-5", orca: "h-[15px] w-5" },
  sm: { box: "h-7 w-7", orca: "h-[21px] w-7" },
  md: { box: "h-10 w-10", orca: "h-[30px] w-10" },
} as const;

export type OrcaStatusSize = keyof typeof SIZES;

type OrcaStatusProps = {
  size?: OrcaStatusSize;
  /** The lane-tinted glow behind the mark. Drop it in already-busy rows. */
  showHalo?: boolean;
  className?: string;
} & (
  | { lane: LaneKey; state?: never; color?: never }
  | {
      lane?: never;
      /** For surfaces with no lane — a chat thread waiting on a reply. */
      state: OrcaState;
      /** Tint, when there is no lane to take one from. Defaults to the brand blue. */
      color?: string;
    }
);

export function OrcaStatus({
  lane,
  state: explicitState,
  color,
  size = "xs",
  showHalo = true,
  className,
}: OrcaStatusProps) {
  const state = explicitState ?? (lane ? LANE_STATE[lane] : undefined) ?? "idle";
  const s = SIZES[size];
  const tint = lane ? (LANE_MAP[lane]?.color ?? "var(--color-lane-drafting)") : (color ?? "var(--color-primary-400)");

  return (
    <span
      className={cn("relative inline-flex shrink-0 items-center justify-center", s.box, className)}
      // One colour drives both the halo and the eye, so a card's mascot always
      // agrees with its lane pill.
      style={{ color: tint }}
      aria-hidden="true"
    >
      {showHalo && state !== "idle" && (
        <span
          className="absolute inset-0 rounded-full opacity-20 blur-[6px]"
          style={{ backgroundColor: "currentColor" }}
        />
      )}
      <OrcaSvg eyeColor="currentColor" className={cn("relative", s.orca, ANIMATION[state])} />
    </span>
  );
}
