import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";

interface OrcaErrorProps {
  size?: "sm" | "md" | "lg";
  title?: string;
  description?: string;
  className?: string;
}

const containerSizes = {
  sm: "w-24 h-24",
  md: "w-40 h-40",
  lg: "w-52 h-52",
};

const orcaSvgSizes = {
  sm: "w-10 h-[30px]",
  md: "w-16 h-[48px]",
  lg: "w-20 h-[60px]",
};

function OrcaError({
  size = "md",
  title,
  description,
  className,
}: OrcaErrorProps) {
  return (
    <div className={cn("flex flex-col items-center gap-4", className)}>
      <div className={cn("relative flex items-center justify-center", containerSizes[size])}>
        {/* Error sparks */}
        <div className="absolute inset-0">
          {[0, 0.5, 1].map((delay, i) => (
            <span
              key={i}
              className="absolute text-xs text-error opacity-0 animate-[orca-spark-flash_1.5s_ease-out_infinite]"
              style={{
                top: ["20%", "15%", "40%"][i],
                left: i === 1 ? undefined : ["20%", undefined, "15%"][i],
                right: i === 1 ? "25%" : undefined,
                animationDelay: `${delay}s`,
              }}
            >
              &#10022;
            </span>
          ))}
        </div>
        {/* Upside-down shaking orca */}
        <div className="animate-[orca-tumble_0.5s_ease-in-out_infinite_alternate] [transform:scaleY(-1)]">
          <OrcaSvg eyeColor="#EF4444" expression="dizzy" className={orcaSvgSizes[size]} />
        </div>
        {/* Error X badge */}
        <div className="absolute bottom-[15px] left-1/2 flex h-8 w-8 items-center justify-center rounded-full bg-error animate-[orca-error-shake_0.6s_ease-in-out_infinite]">
          <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="white" strokeWidth="3" strokeLinecap="round">
            <line x1="7" y1="7" x2="17" y2="17" />
            <line x1="17" y1="7" x2="7" y2="17" />
          </svg>
        </div>
      </div>
      {title && (
        <span className="font-display text-sm font-semibold text-gray-200">
          {title}
        </span>
      )}
      {description && (
        <span className="text-xs text-gray-400 text-center max-w-[200px]">
          {description}
        </span>
      )}
    </div>
  );
}

export { OrcaError };
