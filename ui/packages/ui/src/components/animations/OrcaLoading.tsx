import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";

interface OrcaLoadingProps {
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

const ringSizes = {
  sm: "w-20 h-20",
  md: "w-30 h-30",
  lg: "w-40 h-40",
};

const orcaSvgSizes = {
  sm: "w-10 h-[30px]",
  md: "w-16 h-[48px]",
  lg: "w-20 h-[60px]",
};

function OrcaLoading({
  size = "md",
  title,
  description,
  className,
}: OrcaLoadingProps) {
  return (
    <div className={cn("flex flex-col items-center gap-4", className)}>
      <div className={cn("relative flex items-center justify-center", containerSizes[size])}>
        {/* Spinning ring */}
        <div
          className={cn(
            "rounded-full border-3 border-orca-body border-t-[#3B82F6]",
            "animate-[orca-spin_1.2s_linear_infinite]",
            ringSizes[size]
          )}
        />
        {/* Orca center */}
        <div className="absolute top-1/2 left-1/2 animate-[orca-pulse_1.2s_ease-in-out_infinite]">
          <OrcaSvg className={orcaSvgSizes[size]} />
        </div>
        {/* Bubbles */}
        <div className="absolute inset-0">
          {[0, 0.5, 1].map((delay, i) => (
            <span
              key={i}
              className="absolute rounded-full bg-primary-400 opacity-0 animate-[orca-bubble-rise_2s_ease-in_infinite]"
              style={{
                width: [6, 4, 5][i],
                height: [6, 4, 5][i],
                left: ["45%", "55%", "50%"][i],
                animationDelay: `${delay}s`,
              }}
            />
          ))}
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

export { OrcaLoading };
