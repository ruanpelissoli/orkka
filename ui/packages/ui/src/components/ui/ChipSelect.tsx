import { cn } from "@/lib/utils";

interface ChipSelectOption {
  value: string;
  label: string;
}

interface ChipSelectProps {
  value: string;
  options: ChipSelectOption[];
  onChange: (value: string) => void;
  /** Names the field, e.g. "Repo". Rendered inside the chip because a bare value is
   * indistinguishable from a status badge — "tapago-mobile" alone reads as a fact. */
  label: string;
  /** Shown as the selected text when `value` is empty. */
  placeholder?: string;
  /** Draws attention when the empty state actually blocks something (a task with no repo
   * cannot be submitted). */
  emphasizeEmpty?: boolean;
  disabled?: boolean;
  className?: string;
}

/**
 * A `<select>` that looks like a chip but never like a *badge*.
 *
 * Task detail renders read-only badges (lane, status) and editable pickers in one row. They
 * were styled the same — same pill, same border, same weight, no chevron — so the pickers
 * read as labels and nobody could tell they were controls. This carries the affordances the
 * badges deliberately lack: a field label, a chevron, a hover shift, and a focus ring.
 *
 * The native select is kept and simply made transparent on top of the visual chip. A custom
 * dropdown would lose keyboard behaviour, mobile's native picker, and typeahead — none of
 * which is worth re-implementing to restyle a list.
 */
export function ChipSelect({
  value,
  options,
  onChange,
  label,
  placeholder,
  emphasizeEmpty = false,
  disabled = false,
  className,
}: ChipSelectProps) {
  const selected = options.find((o) => o.value === value);
  const isEmpty = !selected;
  const alert = isEmpty && emphasizeEmpty;

  return (
    <span
      className={cn(
        "group relative inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5",
        "text-xs font-medium transition-colors",
        disabled
          ? "cursor-not-allowed border-white/[0.06] bg-white/[0.02] text-gray-400"
          : alert
            ? "cursor-pointer border-amber-500/40 bg-amber-500/10 text-amber-300 hover:bg-amber-500/20"
            : // Dashed border is the cue that reads as "editable" at a glance, next to the
              // solid-bordered badges. Cheap, and it survives colour-blind vision.
              "cursor-pointer border-dashed border-white/[0.18] bg-white/[0.04] text-gray-300 hover:border-white/[0.35] hover:bg-white/[0.08]",
        "focus-within:border-primary-500/60 focus-within:ring-2 focus-within:ring-primary-500/20",
        className
      )}
    >
      <span className="text-gray-400">{label}</span>
      <span className={cn(isEmpty && !alert && "text-gray-400")}>
        {selected?.label ?? placeholder ?? "—"}
      </span>

      {/* Chevron: the single clearest "this opens something" signal, and the thing whose
          absence made these read as static text. */}
      {!disabled && (
        <svg
          width="10"
          height="10"
          viewBox="0 0 16 16"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          aria-hidden="true"
          className="shrink-0 text-gray-400 transition-colors group-hover:text-gray-300"
        >
          <path d="M4 6l4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      )}

      {/* The real control, invisible but on top — keyboard, mobile pickers and typeahead
          all keep working. */}
      <select
        value={value}
        disabled={disabled}
        aria-label={label}
        onChange={(e) => onChange(e.target.value)}
        className="absolute inset-0 cursor-pointer opacity-0 disabled:cursor-not-allowed"
      >
        {isEmpty && (
          <option value="">
            {placeholder ?? "—"}
          </option>
        )}
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </span>
  );
}
