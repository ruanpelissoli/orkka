import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";

interface OrcaWorkingProps {
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

const codeSnippets = ["</>", "{ }", "fn()", "=>"];

function OrcaWorking({
  size = "md",
  title,
  description,
  className,
}: OrcaWorkingProps) {
  return (
    <div className={cn("flex flex-col items-center gap-4", className)}>
      <div className={cn("relative flex items-center justify-center", containerSizes[size])}>
        {/* Diving orca */}
        <div className="animate-[orca-dive_2.5s_ease-in-out_infinite]">
          <OrcaSvg className={orcaSvgSizes[size]} />
        </div>
        {/* Code particles */}
        <div className="pointer-events-none absolute inset-0">
          {codeSnippets.map((code, i) => (
            <span
              key={i}
              className="absolute text-xs text-primary-400 opacity-0 animate-[orca-code-float_3s_ease-out_infinite]"
              style={{
                left: ["10%", "75%", "20%", "70%"][i],
                top: ["20%", "30%", "60%", "70%"][i],
                animationDelay: `${[0, 0.8, 1.5, 2.2][i]}s`,
              }}
            >
              {code}
            </span>
          ))}
        </div>
      </div>
      {/* Progress bar */}
      <div className="w-30 h-1 rounded-full bg-orca-body overflow-hidden">
        <div className="h-full rounded-full bg-gradient-to-r from-primary-500 to-primary-300 animate-[orca-progress-sweep_2s_ease-in-out_infinite]" />
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

export { OrcaWorking };
