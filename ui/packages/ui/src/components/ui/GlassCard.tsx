import { type HTMLAttributes } from "react";
import { cn } from "@/lib/utils";

interface GlassCardProps extends HTMLAttributes<HTMLDivElement> {
  hover?: boolean;
  accentColor?: string;
  glow?: boolean;
  padding?: "none" | "sm" | "md" | "lg";
}

function GlassCard({
  className,
  hover = false,
  accentColor,
  glow = false,
  padding = "md",
  children,
  style,
  ...props
}: GlassCardProps) {
  const paddingClasses = {
    none: "",
    sm: "p-3",
    md: "p-5",
    lg: "p-7",
  };

  return (
    <div
      className={cn(
        "relative rounded-2xl overflow-hidden",
        "bg-white/[0.03] backdrop-blur-xl",
        "border border-white/[0.06]",
        "transition-all duration-300 ease-out",
        hover && [
          "hover:bg-white/[0.06]",
          "hover:border-white/[0.12]",
          "hover:shadow-[0_8px_32px_rgba(0,0,0,0.3),inset_0_1px_0_rgba(255,255,255,0.05)]",
          "hover:-translate-y-0.5",
        ],
        glow && "shadow-[0_0_30px_rgba(59,130,246,0.08)]",
        paddingClasses[padding],
        className
      )}
      style={style}
      {...props}
    >
      {/* Colored accent line at top */}
      {accentColor && (
        <div
          className="absolute top-0 left-0 right-0 h-[2px]"
          style={{
            background: `linear-gradient(90deg, transparent, ${accentColor}, transparent)`,
          }}
        />
      )}
      {/* Inner highlight line */}
      <div className="pointer-events-none absolute inset-x-0 top-0 h-px bg-gradient-to-r from-transparent via-white/[0.08] to-transparent" />
      {children}
    </div>
  );
}

export { GlassCard };
export type { GlassCardProps };
