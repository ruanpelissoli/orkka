import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";

interface OrcaSuccessProps {
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

const splashOffsets = [
  { x: -25, y: -35, delay: 0.6 },
  { x: 20, y: -40, delay: 0.7 },
  { x: -15, y: -45, delay: 0.65 },
  { x: 30, y: -30, delay: 0.75 },
  { x: -35, y: -25, delay: 0.68 },
  { x: 10, y: -50, delay: 0.72 },
];

function OrcaSuccess({
  size = "md",
  title,
  description,
  className,
}: OrcaSuccessProps) {
  return (
    <div className={cn("flex flex-col items-center gap-4", className)}>
      <div className={cn("relative flex items-center justify-center", containerSizes[size])}>
        {/* Breaching orca */}
        <div className="animate-[orca-breach_2s_ease-out_infinite]">
          <OrcaSvg eyeColor="#10b981" className={orcaSvgSizes[size]} />
        </div>
        {/* Splash particles */}
        <div className="pointer-events-none absolute inset-0">
          {splashOffsets.map((p, i) => (
            <span
              key={i}
              className="absolute bottom-[30%] left-1/2 h-1 w-1 rounded-full bg-success opacity-0 animate-[orca-splash_2s_ease-out_infinite]"
              style={{
                "--splash-x": `${p.x}px`,
                "--splash-y": `${p.y}px`,
                animationDelay: `${p.delay}s`,
              } as React.CSSProperties}
            />
          ))}
        </div>
        {/* Check badge */}
        <div className="absolute bottom-[15px] left-1/2 flex h-8 w-8 items-center justify-center rounded-full bg-success animate-[orca-check-pop_2s_ease-out_infinite]">
          <svg viewBox="0 0 24 24" className="h-[18px] w-[18px]" fill="none" stroke="white" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round">
            <polyline points="6 12 10 16 18 8" />
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

export { OrcaSuccess };
