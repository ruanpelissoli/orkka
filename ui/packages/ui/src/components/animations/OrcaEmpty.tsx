import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";

interface OrcaEmptyProps {
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

function OrcaEmpty({
  size = "md",
  title,
  description,
  className,
}: OrcaEmptyProps) {
  return (
    <div className={cn("flex flex-col items-center gap-4", className)}>
      <div className={cn("relative flex items-center justify-center", containerSizes[size])}>
        {/* Zzz */}
        <div className="absolute top-[10px] right-[25px] text-sm font-bold text-primary-300">
          {[0, 0.7, 1.4].map((delay, i) => (
            <span
              key={i}
              className="inline-block opacity-0 animate-[orca-zzz-float_3s_ease-out_infinite]"
              style={{
                animationDelay: `${delay}s`,
                fontSize: [10, 13, 16][i],
              }}
            >
              z
            </span>
          ))}
        </div>
        {/* Sleeping orca */}
        <div className="animate-[orca-sleep_4s_ease-in-out_infinite]">
          <OrcaSvg eyeColor="#93C5FD" expression="sleeping" className={orcaSvgSizes[size]} />
        </div>
        {/* Wave line */}
        <div className="absolute bottom-[35px] left-1/2 -translate-x-1/2 w-30 h-0.5 overflow-visible">
          <svg width="160" height="8" viewBox="0 0 160 8" className="animate-[orca-wave-shift_3s_linear_infinite]">
            <path
              d="M0,4 Q10,0 20,4 T40,4 T60,4 T80,4 T100,4 T120,4 T140,4 T160,4"
              fill="none"
              stroke="#1E2D4D"
              strokeWidth="1.5"
            />
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

export { OrcaEmpty };
