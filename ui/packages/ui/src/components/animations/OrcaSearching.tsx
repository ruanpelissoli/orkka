import { cn } from "@/lib/utils";
import { OrcaSvg } from "./OrcaSvg";

interface OrcaSearchingProps {
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

function OrcaSearching({
  size = "md",
  title,
  description,
  className,
}: OrcaSearchingProps) {
  return (
    <div className={cn("flex flex-col items-center gap-4", className)}>
      <div className={cn("relative flex items-center justify-center", containerSizes[size])}>
        {/* Searching orca */}
        <div className="animate-[orca-search_2.5s_ease-in-out_infinite]">
          <OrcaSvg className={orcaSvgSizes[size]} />
        </div>
        {/* Scan beam */}
        <div
          className="absolute top-[30%] left-[55%] h-0.5 w-15 rounded-sm bg-gradient-to-r from-primary-400 to-transparent origin-left animate-[orca-beam-scan_2.5s_ease-in-out_infinite]"
        />
        {/* Scan dots */}
        <div className="absolute inset-0">
          {[0.3, 0.8, 1.3].map((delay, i) => (
            <span
              key={i}
              className="absolute h-[3px] w-[3px] rounded-full bg-primary-300 opacity-0 animate-[orca-scan-dot-pop_2.5s_ease-out_infinite]"
              style={{
                right: ["15%", "10%", "20%"][i],
                top: ["25%", "35%", "45%"][i],
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

export { OrcaSearching };
