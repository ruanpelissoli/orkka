import { forwardRef, type InputHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  label?: string;
  error?: string;
  hint?: string;
}

const Input = forwardRef<HTMLInputElement, InputProps>(
  ({ className, label, error, hint, id, ...props }, ref) => {
    const inputId = id || label?.toLowerCase().replace(/\s+/g, "-");

    return (
      <div className="space-y-1.5">
        {label && (
          <label
            htmlFor={inputId}
            className="block text-sm font-medium text-gray-300"
          >
            {label}
          </label>
        )}
        <div className="group relative">
          <input
            ref={ref}
            id={inputId}
            className={cn(
              "w-full rounded-xl px-4 py-2.5 text-sm text-gray-100",
              "bg-white/[0.03] backdrop-blur-sm",
              "border border-white/[0.06]",
              "placeholder:text-gray-500",
              "transition-all duration-200",
              "focus:bg-white/[0.06] focus:border-primary-500/50",
              "focus:shadow-[0_0_0_3px_rgba(59,130,246,0.1),0_0_20px_rgba(59,130,246,0.1)]",
              "focus:outline-none",
              error && "border-error/50 focus:border-error/50 focus:shadow-[0_0_0_3px_rgba(239,68,68,0.1)]",
              className
            )}
            {...props}
          />
          {/* Animated gradient border on focus */}
          <div
            className={cn(
              "pointer-events-none absolute inset-0 rounded-xl opacity-0 transition-opacity duration-300",
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

Input.displayName = "Input";

export { Input };
