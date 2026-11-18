/**
 * The Orkka brand artwork, at whatever size the caller asks for.
 *
 * A plain `<img>` of the SVG served from /public, untouched.
 *
 * The `pro` variant animates: it carries its own glint keyframes and reduced-motion opt-out
 * inside the file, because an SVG loaded through `<img>` cannot see the page's stylesheet.
 *
 * The `lettering` and `full` variants carry the wordmark as outlined Unbounded paths.
 * They are loaded through `<img>`, which cannot reach a webfont — so if you ever need to
 * change the lettering, regenerate the outlines. Live `<text>` in those files silently
 * falls back to a system face and draws the brand in a different typeface on every OS,
 * which is exactly what they used to do.
 */
export type BrandMarkVariant = "icon" | "orca" | "lettering" | "full" | "pro";

const SOURCES: Record<BrandMarkVariant, string> = {
  /** The square app mark. */
  icon: "/orkka-logo-icon.svg",
  /** The swimming orca illustration, no container. */
  orca: "/orkka-orca.svg",
  /** "Orkka" on its own, outlined. */
  lettering: "/orkka-logo-lettering.svg",
  /** Mark + wordmark, horizontal. */
  full: "/orkka-logo-full.svg",
  /** The orca in gold — the Pro plan mark. Cropped to the creature so it reads at badge size. */
  pro: "/orkka-logo-pro.svg",
};

export function BrandMark({
  className,
  variant = "icon",
  alt = "Orkka",
}: {
  className?: string;
  variant?: BrandMarkVariant;
  /** Pass "" when a sibling already names the brand, so it isn't read out twice. */
  alt?: string;
}) {
  return <img src={SOURCES[variant]} alt={alt} className={className} />;
}
