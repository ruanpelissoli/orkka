import { type HTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

const badgeVariants = cva(
  [
    "inline-flex items-center gap-1.5 rounded-full text-xs font-medium",
    // Pills have a fixed height — wrapped text overflows the border, so never wrap inside
    // a badge; tight containers should wrap BETWEEN badges (flex-wrap on the row).
    "whitespace-nowrap",
    "transition-all duration-200",
  ],
  {
    variants: {
      variant: {
        default: "bg-white/[0.06] text-gray-300 border border-white/[0.06]",
        primary: "bg-primary-500/10 text-primary-400 border border-primary-500/20",
        success: "bg-emerald-500/10 text-emerald-400 border border-emerald-500/20",
        warning: "bg-amber-500/10 text-amber-400 border border-amber-500/20",
        error: "bg-red-500/10 text-red-400 border border-red-500/20",
        info: "bg-cyan-500/10 text-cyan-400 border border-cyan-500/20",
        purple: "bg-violet-500/10 text-violet-400 border border-violet-500/20",
      },
      size: {
        sm: "h-5 px-2 text-xs",
        md: "h-6 px-2.5 text-xs",
        lg: "h-7 px-3 text-xs",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "md",
    },
  }
);

interface BadgeProps
  extends HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {
  dot?: boolean;
  dotColor?: string;
  glow?: boolean;
}

function Badge({
  className,
  variant,
  size,
  dot = false,
  dotColor,
  glow = false,
  children,
  style,
  ...props
}: BadgeProps) {
  return (
    <span
      className={cn(
        badgeVariants({ variant, size }),
        glow && "shadow-[0_0_12px_currentColor/0.2]",
        className
      )}
      style={style}
      {...props}
    >
      {dot && (
        <span
          className="h-1.5 w-1.5 rounded-full shrink-0"
          style={{
            backgroundColor: dotColor || "currentColor",
            boxShadow: glow ? `0 0 6px ${dotColor || "currentColor"}` : undefined,
          }}
        />
      )}
      {children}
    </span>
  );
}

export { Badge, badgeVariants };
export type { BadgeProps };
