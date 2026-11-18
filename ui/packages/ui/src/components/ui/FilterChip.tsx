import { cn } from "@/lib/utils";

interface FilterChipProps {
  label: string;
  /** Rendered beside the label. A chip that cannot say how much it would show is a guess. */
  count?: number;
  active: boolean;
  onToggle: () => void;
  /** Accent for the active state — a lane or status colour, so a chip looks like what it filters. */
  color?: string;
  className?: string;
}

/**
 * An independent on/off filter pill.
 *
 * `aria-pressed`, not a radio: these combine, and turning one off does not turn another on.
 * A chip with nothing to show renders disabled rather than disappearing — a row that reflows as
 * work moves through the board is a row you cannot aim at.
 */
function FilterChip({ label, count, active, onToggle, color, className }: FilterChipProps) {
  const empty = count === 0;

  return (
    <button
      type="button"
      aria-pressed={active}
      disabled={empty && !active}
      onClick={onToggle}
      className={cn(
        "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-medium",
        "transition-colors",
        active
          ? "border-white/[0.12] bg-white/[0.10] text-white"
          : "border-white/[0.06] bg-white/[0.03] text-gray-400 hover:bg-white/[0.06] hover:text-gray-200",
        empty && !active && "cursor-default opacity-40 hover:bg-white/[0.03] hover:text-gray-400",
        className
      )}
      style={
        active && color
          ? { borderColor: `${color}55`, backgroundColor: `${color}1a` }
          : undefined
      }
    >
      {color && (
        <span
          className="h-1.5 w-1.5 shrink-0 rounded-full"
          style={{ backgroundColor: color }}
        />
      )}
      {label}
      {count !== undefined && (
        <span className={cn("tabular-nums", active ? "text-gray-300" : "text-gray-400")}>
          {count}
        </span>
      )}
    </button>
  );
}

export { FilterChip };
export type { FilterChipProps };
