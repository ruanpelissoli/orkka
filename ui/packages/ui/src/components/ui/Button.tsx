import { forwardRef, type ButtonHTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

const buttonVariants = cva(
  [
    "relative inline-flex items-center justify-center gap-2 font-display font-semibold",
    "rounded-xl transition-all duration-200 ease-out",
    "disabled:pointer-events-none disabled:opacity-40",
    "active:scale-[0.97] cursor-pointer",
  ],
  {
    variants: {
      variant: {
        primary: [
          "bg-gradient-to-r from-primary-600 to-primary-500 text-white",
          "shadow-[0_0_20px_rgba(59,130,246,0.15)]",
          "hover:shadow-[0_0_30px_rgba(59,130,246,0.3)] hover:scale-[1.02]",
          "hover:from-primary-500 hover:to-primary-400",
        ],
        secondary: [
          "glass text-gray-200",
          "hover:bg-white/[0.08] hover:border-white/[0.15]",
          "hover:shadow-[0_0_20px_rgba(255,255,255,0.05)]",
        ],
        ghost: [
          "text-gray-400 bg-transparent",
          "hover:text-gray-100 hover:bg-white/[0.05]",
        ],
        danger: [
          "bg-gradient-to-r from-red-600 to-red-500 text-white",
          "shadow-[0_0_20px_rgba(239,68,68,0.15)]",
          "hover:shadow-[0_0_30px_rgba(239,68,68,0.3)] hover:scale-[1.02]",
        ],
        success: [
          "bg-gradient-to-r from-emerald-600 to-emerald-500 text-white",
          "shadow-[0_0_20px_rgba(16,185,129,0.15)]",
          "hover:shadow-[0_0_30px_rgba(16,185,129,0.3)] hover:scale-[1.02]",
        ],
      },
      size: {
        sm: "h-8 px-3 text-xs rounded-lg",
        md: "h-10 px-5 text-sm",
        lg: "h-12 px-7 text-base",
        xl: "h-14 px-9 text-lg",
      },
    },
    defaultVariants: {
      variant: "primary",
      size: "md",
    },
  }
);

interface ButtonProps
  extends ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {
  loading?: boolean;
}

const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, loading, children, disabled, ...props }, ref) => {
    return (
      <button
        ref={ref}
        className={cn(buttonVariants({ variant, size }), className)}
        disabled={disabled || loading}
        {...props}
      >
        {loading && (
          <span className="absolute inset-0 flex items-center justify-center">
            <span className="h-4 w-4 animate-spin rounded-full border-2 border-current border-t-transparent" />
          </span>
        )}
        <span className={cn("inline-flex items-center gap-2", loading && "invisible")}>{children}</span>
      </button>
    );
  }
);

Button.displayName = "Button";

export { Button, buttonVariants };
export type { ButtonProps };
