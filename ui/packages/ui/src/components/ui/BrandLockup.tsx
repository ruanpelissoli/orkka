import { cn } from "@/lib/utils";
import { BrandMark } from "@/components/ui/BrandMark";

/**
 * Mark + "Orkka", locked together.
 *
 * This is the only way the product name is allowed to be typeset. It used to be
 * hand-rolled at seven call sites — navbar, footer, both sidebars, the mobile top
 * bar, the auth shell and the invite page — which is how the mark size, the gap and
 * the type size drifted apart between them. Adding a new placement means calling
 * this, not copying a `<span>`.
 *
 * The wordmark is live text rather than the `lettering` SVG on purpose: inside the
 * app the Unbounded webfont is already loaded, so real text stays crisp at every
 * size, respects the user's text scaling and is selectable. The SVG variants exist
 * for contexts that cannot reach the webfont — social cards, email, README.
 */

const SIZES = {
  sm: { mark: "h-7 w-7", text: "text-base" },
  md: { mark: "h-8 w-8", text: "text-lg" },
  lg: { mark: "h-10 w-10", text: "text-xl" },
  xl: { mark: "h-14 w-14", text: "text-3xl" },
} as const;

export type BrandLockupSize = keyof typeof SIZES;

export function BrandLockup({
  size = "md",
  markOnly = false,
  className,
}: {
  size?: BrandLockupSize;
  /** Collapsed rail: drop the wordmark but keep the mark's sizing. */
  markOnly?: boolean;
  className?: string;
}) {
  const s = SIZES[size];

  return (
    <span className={cn("flex items-center gap-2.5", className)}>
      <BrandMark
        variant="icon"
        // The wordmark beside it already says "Orkka"; alt here would double it up.
        alt={markOnly ? "Orkka" : ""}
        className={cn(s.mark, "shrink-0 rounded-lg")}
      />
      {!markOnly && <span className={cn("font-brand font-bold text-white", s.text)}>Orkka</span>}
    </span>
  );
}
