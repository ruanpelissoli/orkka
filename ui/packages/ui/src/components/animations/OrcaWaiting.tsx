import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";

interface OrcaWaitingProps {
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

function OrcaWaiting({
  size = "md",
  title,
  description,
  className,
}: OrcaWaitingProps) {
  return (
    <div className={cn("flex flex-col items-center gap-4", className)}>
      <div className={cn("relative flex items-center justify-center", containerSizes[size])}>
        {/* Question mark */}
        <span className="absolute top-[15px] right-[25px] text-2xl font-extrabold text-warning animate-[orca-question-pop_2s_ease-in-out_infinite]">
          ?
        </span>
        {/* Floating orca */}
        <div className="animate-[orca-float_3s_ease-in-out_infinite]">
          <OrcaSvg eyeColor="#F59E0B" className={orcaSvgSizes[size]} />
        </div>
      </div>
      {/* Bouncing dots */}
      <div className="flex gap-1.5">
        {[0, 0.2, 0.4].map((delay, i) => (
          <span
            key={i}
            className="h-2 w-2 rounded-full bg-warning animate-[orca-dot-bounce_1.4s_ease-in-out_infinite]"
            style={{ animationDelay: `${delay}s` }}
          />
        ))}
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

export { OrcaWaiting };
