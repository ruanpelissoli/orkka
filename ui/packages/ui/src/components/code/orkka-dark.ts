import type { ThemeRegistrationRaw } from "shiki/core";

/**
 * The one syntax theme for every place Orkka shows code — file viewer, diffs, markdown
 * fences, plans. Built from the design tokens in packages/ui/src/styles/theme.css so code
 * sits in the same palette as the chrome around it:
 *
 *   keywords / storage      violet-300   #c4b5fd
 *   strings                 emerald-300  #6ee7b7
 *   functions / methods     primary-300  #93bbfd
 *   types / classes / tags  cyan-300     #67e8f9
 *   numbers / constants     amber-300    #fcd34d
 *   properties / attrs      primary-200  #bfdbfe · violet-200 #ddd6fe
 *   comments                gray-400     #9ca3af (italic)
 *   plain text              gray-200     #e5e7eb
 *   ground                  surface-sunken #070b16
 *
 * Diff add/delete are row tints applied by the diff renderer, never token colours — that
 * is what keeps a green "+" line readable instead of one flat block of green text.
 *
 * The design system is dark-only, so this is the only theme; the name is what consumers
 * pass to shiki as `theme`.
 */
const ORKKA_DARK_THEME_NAME = "orkka-dark";

const orkkaDark: ThemeRegistrationRaw = {
  name: ORKKA_DARK_THEME_NAME,
  displayName: "Orkka Dark",
  type: "dark",
  fg: "#e5e7eb",
  bg: "#070b16",
  colors: {
    "editor.background": "#070b16",
    "editor.foreground": "#e5e7eb",
  },
  settings: [
    // Scope-less first entry = the global foreground/background, per TextMate convention.
    { settings: { foreground: "#e5e7eb", background: "#070b16" } },
    {
      scope: ["comment", "punctuation.definition.comment"],
      settings: { foreground: "#9ca3af", fontStyle: "italic" },
    },
    {
      scope: [
        "keyword",
        "storage",
        "storage.type",
        "storage.modifier",
        "keyword.control",
        "keyword.other",
        "keyword.operator.new",
        "keyword.operator.expression",
        "keyword.operator.typeof",
        "keyword.operator.instanceof",
        "keyword.operator.delete",
        "keyword.operator.void",
        "keyword.operator.await",
        "variable.language",
      ],
      settings: { foreground: "#c4b5fd" },
    },
    {
      scope: [
        "keyword.operator",
        "punctuation",
        "meta.brace",
        "punctuation.separator",
        "punctuation.terminator",
        "punctuation.accessor",
        "punctuation.definition.tag",
      ],
      settings: { foreground: "#9ca3af" },
    },
    {
      scope: ["string", "string.quoted", "punctuation.definition.string"],
      settings: { foreground: "#6ee7b7" },
    },
    {
      scope: ["string.regexp", "constant.character.escape", "constant.character"],
      settings: { foreground: "#fde68a" },
    },
    {
      scope: [
        "constant.numeric",
        "constant.language",
        "constant.language.boolean",
        "constant.other",
        "variable.other.constant",
        "support.constant",
        "entity.name.label",
        "keyword.other.unit",
      ],
      settings: { foreground: "#fcd34d" },
    },
    {
      scope: [
        "entity.name.function",
        "support.function",
        "entity.name.method",
        "meta.function-call entity.name.function",
        "variable.function",
      ],
      settings: { foreground: "#93bbfd" },
    },
    {
      scope: [
        "entity.name.type",
        "entity.name.class",
        "entity.name.interface",
        "entity.name.enum",
        "entity.name.struct",
        "entity.name.namespace",
        "entity.other.inherited-class",
        "entity.name.type.parameter",
        "support.type",
        "support.class",
        "support.type.primitive",
        "keyword.type",
        "entity.name.tag",
      ],
      settings: { foreground: "#67e8f9" },
    },
    {
      scope: [
        "variable",
        "variable.other",
        "variable.parameter",
        "meta.definition.variable",
        "entity.name.variable",
      ],
      settings: { foreground: "#e5e7eb" },
    },
    {
      scope: [
        "variable.other.property",
        "variable.other.object.property",
        "support.variable.property",
        "meta.object-literal.key",
        "support.type.property-name",
        "entity.name.tag.yaml",
        "source.css support.type.property-name",
      ],
      settings: { foreground: "#bfdbfe" },
    },
    {
      scope: ["entity.other.attribute-name"],
      settings: { foreground: "#ddd6fe" },
    },
    {
      scope: [
        "meta.decorator",
        "punctuation.decorator",
        "entity.name.function.decorator",
        "keyword.control.directive",
        "meta.preprocessor",
        "punctuation.definition.template-expression",
        "punctuation.section.embedded",
      ],
      settings: { foreground: "#a78bfa" },
    },
    {
      scope: ["source.css entity.other.attribute-name.class", "source.css entity.name.tag"],
      settings: { foreground: "#93bbfd" },
    },
    // Markdown
    {
      scope: ["markup.heading", "entity.name.section", "markup.heading entity.name"],
      settings: { foreground: "#93bbfd", fontStyle: "bold" },
    },
    { scope: ["markup.bold"], settings: { fontStyle: "bold" } },
    { scope: ["markup.italic"], settings: { fontStyle: "italic" } },
    {
      scope: ["markup.underline.link", "string.other.link"],
      settings: { foreground: "#60a5fa", fontStyle: "underline" },
    },
    {
      scope: ["markup.inline.raw", "markup.fenced_code", "markup.raw.block"],
      settings: { foreground: "#6ee7b7" },
    },
    { scope: ["markup.quote"], settings: { foreground: "#9ca3af", fontStyle: "italic" } },
    { scope: ["markup.list punctuation.definition.list.begin"], settings: { foreground: "#c4b5fd" } },
    // Diff / patch language
    { scope: ["markup.inserted", "meta.diff.header.to-file"], settings: { foreground: "#6ee7b7" } },
    { scope: ["markup.deleted", "meta.diff.header.from-file"], settings: { foreground: "#fca5a5" } },
    { scope: ["markup.changed"], settings: { foreground: "#fcd34d" } },
    { scope: ["meta.diff.range", "meta.diff.header", "meta.diff.index"], settings: { foreground: "#9ca3af" } },
    { scope: ["invalid", "invalid.illegal"], settings: { foreground: "#fca5a5" } },
  ],
};

export { orkkaDark, ORKKA_DARK_THEME_NAME };
