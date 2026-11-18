import { cn } from "@/lib/utils";

interface SegmentedControlOption<T extends string> {
  value: T;
  label: string;
  /** Tooltip / screen-reader text when the label alone is too terse to be clear. */
  title?: string;
  testId?: string;
}

interface SegmentedControlProps<T extends string> {
  options: readonly SegmentedControlOption<T>[];
  value: T;
  onChange: (value: T) => void;
  /** Names the group for screen readers — a radiogroup with no label is an unnamed choice. */
  "aria-label"?: string;
  className?: string;
}

/**
 * The pill-in-a-trough single-select used by the stats window switchers, the activity view
 * toggle and the board's metric window.
 *
 * It is a radiogroup, not a set of toggle buttons: exactly one option is always chosen, which
 * `aria-pressed` cannot express. Three hand-rolled copies of this markup drifted into two
 * different ARIA spellings before it was extracted — this is the one that is correct.
 */
function SegmentedControl<T extends string>({
  options,
  value,
  onChange,
  className,
  "aria-label": ariaLabel,
}: SegmentedControlProps<T>) {
  return (
    <div
      role="radiogroup"
      aria-label={ariaLabel}
      className={cn(
        "inline-flex rounded-lg border border-white/[0.08] bg-white/[0.03] p-0.5 text-xs",
        className
      )}
    >
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          role="radio"
          aria-checked={value === option.value}
          title={option.title}
          data-testid={option.testId}
          onClick={() => onChange(option.value)}
          className={cn(
            "rounded-md px-2.5 py-1 transition-colors",
            value === option.value
              ? "bg-white/[0.08] text-white"
              : "text-gray-400 hover:text-gray-200"
          )}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

export { SegmentedControl };
export type { SegmentedControlOption, SegmentedControlProps };
