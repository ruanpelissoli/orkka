import { forwardRef, type SelectHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

/**
 * `size` shadows the native `size` attribute (the number of visible rows on a multi-row
 * listbox), which this product never uses — a dropdown is always collapsed. Trading it for
 * the density variant lets compact toolbar filters use the same component as form fields,
 * which is the whole reason they had drifted into hand-rolled copies.
 */
interface SelectProps extends Omit<SelectHTMLAttributes<HTMLSelectElement>, "size"> {
  label?: string;
  error?: string;
  hint?: string;
  /** `md` for form fields, `sm` for toolbar filters and dock headers. */
  size?: "sm" | "md";
}

const Select = forwardRef<HTMLSelectElement, SelectProps>(
  ({ className, label, error, hint, id, size = "md", children, ...props }, ref) => {
    const selectId = id || label?.toLowerCase().replace(/\s+/g, "-");
    const compact = size === "sm";

    return (
      <div className="space-y-1.5">
        {label && (
          <label
            htmlFor={selectId}
            className="block text-sm font-medium text-gray-300"
          >
            {label}
          </label>
        )}
        <div className="group relative">
          <select
            ref={ref}
            id={selectId}
            className={cn(
              // pr-* leaves the chevron its own lane; without it a long option label runs
              // straight underneath the icon.
              "w-full truncate text-gray-100",
              compact
                ? "rounded-lg pl-3 pr-8 py-1.5 text-xs"
                : "rounded-xl pl-4 pr-10 py-2.5 text-sm",
              "bg-white/[0.03] backdrop-blur-sm",
              "border border-white/[0.06]",
              "transition-all duration-200",
              "hover:border-white/[0.12] hover:bg-white/[0.05]",
              "focus:bg-white/[0.06] focus:border-primary-500/50",
              "focus:shadow-[0_0_0_3px_rgba(59,130,246,0.1),0_0_20px_rgba(59,130,246,0.1)]",
              "focus:outline-none",
              "disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:border-white/[0.06] disabled:hover:bg-white/[0.03]",
              "appearance-none cursor-pointer",
              error && "border-error/50 focus:border-error/50 focus:shadow-[0_0_0_3px_rgba(239,68,68,0.1)]",
              className
            )}
            {...props}
          >
            {children}
          </select>
          {/* Chevron icon */}
          <svg
            width={compact ? 12 : 16}
            height={compact ? 12 : 16}
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            aria-hidden="true"
            className={cn(
              "pointer-events-none absolute top-1/2 -translate-y-1/2 text-gray-400",
              compact ? "right-2.5" : "right-3"
            )}
          >
            <path d="M6 9l6 6 6-6" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          {/* Animated gradient border on focus */}
          <div
            className={cn(
              "pointer-events-none absolute inset-0 opacity-0 transition-opacity duration-300",
              compact ? "rounded-lg" : "rounded-xl",
              "group-focus-within:opacity-100",
              !error && "gradient-border"
            )}
          />
        </div>
        {error && (
          <p className="text-xs text-error/90">{error}</p>
        )}
        {hint && !error && (
          <p className="text-xs text-gray-400">{hint}</p>
        )}
      </div>
    );
  }
);

Select.displayName = "Select";

export { Select };
