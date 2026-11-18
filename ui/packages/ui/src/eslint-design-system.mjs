/**
 * Design-system guard rails, shared by every app's eslint config.
 *
 * These encode the contract documented at the top of
 * packages/ui/src/styles/theme.css. They exist because the codebase had already
 * drifted once: 325 sub-12px type sizes, 654 uses of grey below the AA contrast
 * floor, 14 colour families where the palette has 7, and the word "Orkka"
 * typeset four different ways. Every one of those arrived a single innocuous
 * class at a time, which is what a linter is good at catching.
 *
 * Selectors match string literals and template-literal chunks — where Tailwind
 * classes live. A rule firing on a case that is genuinely correct means the
 * contract should change in theme.css, not that the line needs a disable
 * comment.
 *
 * Usage, in an app's eslint.config.mjs:
 *
 *   import { designSystemRules } from "../../packages/ui/src/eslint-design-system.mjs";
 *   ...
 *   { rules: { "no-restricted-syntax": ["error", ...designSystemRules] } }
 */

const TYPE_SCALE =
  "Off-scale type size. The scale is text-label (12px) / text-section (14px) / " +
  "text-xs / text-sm / text-base / text-lg and up. Nothing in the product renders below 12px.";

export const designSystemRules = [
  // ---- Type scale ----
  {
    selector: "Literal[value=/text-\\[\\d+(\\.\\d+)?(px|rem)\\]/]",
    message: TYPE_SCALE,
  },
  {
    selector: "TemplateElement[value.raw=/text-\\[\\d+(\\.\\d+)?(px|rem)\\]/]",
    message: TYPE_SCALE,
  },
  {
    // The four-class incantation the label utility replaced.
    selector:
      "Literal[value=/(uppercase[^\"]*tracking-wide|tracking-wide[a-z]*[^\"]*uppercase)/]",
    message:
      "Hand-rolled micro-label. Use text-label (12px) or text-section (14px) — they own the face, weight, tracking and colour, so labels cannot drift apart again.",
  },

  // ---- Contrast floor ----
  {
    selector: "Literal[value=/(^|\\s)(!|[\\w.-]+:)*text-gray-600(\\s|$|\\/)/]",
    message:
      "text-gray-600 is 2.5:1 on the app background — far below the 4.5:1 AA floor. Use text-gray-400 for muted text; gray-600 is for borders and dividers only.",
  },
  {
    selector:
      "Literal[value=/(^|\\s)(?!placeholder:|marker:|disabled:|prose-[a-z]+:marker:)([\\w.-]+:)*text-gray-500(\\s|$|\\/)/]",
    message:
      "text-gray-500 is 4.0:1 on the app background. Use text-gray-400 for anything the user reads; gray-500 is allowed only for placeholders, list markers and disabled states.",
  },

  // ---- Palette ----
  {
    selector:
      "Literal[value=/(bg|text|border|from|to|via|ring|shadow|fill|stroke)-\\[#[0-9a-fA-F]{3,8}\\]/]",
    message:
      "Hard-coded colour. Use a token from packages/ui/src/styles/theme.css — the surface ladder (surface-app / raised / sunken / overlay), the primary ramp, the lane tokens, or success/warning/error/info.",
  },
  {
    selector:
      "Literal[value=/(^|\\s)(!|[\\w.-]+:)*(bg|text|border|from|to|via|ring|divide|outline|decoration|fill|stroke)-(blue|sky|teal|fuchsia|orange|yellow|rose|green|slate|zinc|neutral|stone|indigo|purple|pink|lime)-\\d{2,3}/]",
    message:
      "Off-palette colour family. Map it: blue->primary (identical values), sky/teal->cyan, fuchsia/purple/pink->violet, orange/yellow->amber, rose->red, green->emerald, slate/zinc/neutral/stone->gray. The wider agent identity palette is the one sanctioned exception and lives in packages/core AGENT_ROLES, not in a className.",
  },

  // ---- Typefaces ----
  {
    selector: "Literal[value=/(^|\\s)font-(sans|serif)(\\s|$)/]",
    message:
      "font-sans and font-serif resolve to a system stack, not the brand fonts. Use font-display (headings, labels, controls) or font-body (prose) — or nothing, since font-body is the <body> default.",
  },
  {
    selector: "Literal[value=/(^|\\s)font-brand(\\s|$)/]",
    message:
      'font-brand is Unbounded and is reserved for the word "Orkka". Render the product name with <BrandLockup> from @/components/ui, which owns the mark, the gap, the type and the edition pill.',
  },
];

export default designSystemRules;
