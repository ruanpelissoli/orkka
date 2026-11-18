import { cn } from "@/lib/utils";

type OrcaExpression = "normal" | "dizzy" | "sleeping";

interface OrcaSvgProps {
  eyeColor?: string;
  expression?: OrcaExpression;
  className?: string;
}

const sizes = {
  sm: "w-12 h-9",
  md: "w-20 h-15",
  lg: "w-24 h-18",
};

function OrcaSvg({
  eyeColor = "#3B82F6",
  expression = "normal",
  className,
}: OrcaSvgProps) {
  return (
    <svg
      className={cn("w-20 h-[60px]", className)}
      viewBox="0 0 80 60"
      xmlns="http://www.w3.org/2000/svg"
    >
      {/* Body */}
      <path
        d="M12,38 C12,38 18,22 30,17 C38,14 46,13.5 52,15.5 C56.5,17 60,19.5 62,23.5 C64,27.5 63,30.5 60,33 C57,35.5 52,37 46,37.5 C38,38 24,38 15,37.5 Z"
        fill="#1E2D4D"
      />
      {/* Belly */}
      <path
        d="M15,37.5 C24,38 38,38 46,37.5 C52,37 57,35.5 60,33 C59,36.5 55,39 50,40 C44,41.5 36,41 30,39.5 C24,38 17,37.5 15,37.5 Z"
        fill="#9CA3AF"
      />
      {/* Dorsal fin */}
      <path
        d="M40,17 C40,17 39,10 44,7 C44,7 43,12 45,15.5 Z"
        fill="#1E2D4D"
      />
      {/* Tail flukes */}
      {expression !== "dizzy" && (
        <>
          <path
            d="M12,38 C12,38 8,36 5,33 C5,33 7,38 9.5,39 Z"
            fill="#1E2D4D"
          />
          <path
            d="M12,38 C12,38 7.5,39 5,42 C5,42 8,40.5 11,39 Z"
            fill="#1E2D4D"
          />
        </>
      )}
      {/* Eye */}
      <ellipse
        cx="57"
        cy="25"
        rx="3"
        ry="2"
        fill={eyeColor}
        transform="rotate(-15,57,25)"
      />
      {expression === "normal" && (
        <circle cx="58.5" cy="24.5" r="1" fill="#0A0E1A" />
      )}
      {expression === "dizzy" && (
        <>
          <line x1="55" y1="22" x2="57" y2="24" stroke="#EF4444" strokeWidth="0.8" />
          <line x1="57" y1="22" x2="55" y2="24" stroke="#EF4444" strokeWidth="0.8" />
        </>
      )}
      {expression === "sleeping" && (
        <line
          x1="56"
          y1="24.5"
          x2="60"
          y2="24.5"
          stroke="#0A0E1A"
          strokeWidth="1"
          strokeLinecap="round"
        />
      )}
    </svg>
  );
}

export { OrcaSvg, sizes as orcaSizes };
export type { OrcaSvgProps, OrcaExpression };
