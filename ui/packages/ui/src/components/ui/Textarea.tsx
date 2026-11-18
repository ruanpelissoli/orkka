import { forwardRef, type TextareaHTMLAttributes, useRef, useCallback, useState } from "react";
import { cn } from "@/lib/utils";

interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label?: string;
  error?: string;
  showCount?: boolean;
}

const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(
  ({ className, label, error, showCount, maxLength, id, onChange, ...props }, ref) => {
    const inputId = id || label?.toLowerCase().replace(/\s+/g, "-");
    const internalRef = useRef<HTMLTextAreaElement>(null);
    const textareaRef = (ref as React.RefObject<HTMLTextAreaElement>) || internalRef;
    // The character count: the value itself when the field is controlled, else what was typed.
    const [typedLength, setTypedLength] = useState(() => String(props.defaultValue ?? "").length);
    const length = typeof props.value === "string" ? props.value.length : typedLength;

    const handleInput = useCallback(
      (e: React.ChangeEvent<HTMLTextAreaElement>) => {
        const el = e.target;
        el.style.height = "auto";
        el.style.height = el.scrollHeight + "px";
        setTypedLength(el.value.length);
        onChange?.(e);
      },
      [onChange]
    );

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
        <textarea
          ref={textareaRef}
          id={inputId}
          maxLength={maxLength}
          onChange={handleInput}
          className={cn(
            "w-full min-h-[100px] rounded-xl px-4 py-3 text-sm text-gray-100 resize-none",
            "bg-white/[0.03] backdrop-blur-sm",
            "border border-white/[0.06]",
            "placeholder:text-gray-500",
            "transition-all duration-200",
            "focus:bg-white/[0.06] focus:border-primary-500/50",
            "focus:shadow-[0_0_0_3px_rgba(59,130,246,0.1),0_0_20px_rgba(59,130,246,0.1)]",
            "focus:outline-none",
            error && "border-error/50",
            className
          )}
          {...props}
        />
        <div className="flex justify-between">
          {error ? (
            <p className="text-xs text-error/90">{error}</p>
          ) : (
            <span />
          )}
          {showCount && maxLength && (
            <p
              className={cn(
                "text-xs font-mono transition-colors",
                length > maxLength * 0.9 ? "text-warning" : "text-gray-400"
              )}
            >
              {length}/{maxLength}
            </p>
          )}
        </div>
      </div>
    );
  }
);

Textarea.displayName = "Textarea";

export { Textarea };
