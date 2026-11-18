import { createHighlighterCore, type HighlighterCore, type ThemedToken } from "shiki/core";
import { createJavaScriptRegexEngine } from "shiki/engine/javascript";
import { orkkaDark, ORKKA_DARK_THEME_NAME } from "./orkka-dark";
import { LANG_LOADERS, type CodeLang } from "./languages";

/**
 * One highlighter for the whole app. Built lazily on first use from `shiki/core` with the
 * JavaScript regex engine (no wasm to ship or await) and no grammars — each language is
 * loaded the first time something asks for it, so a page that only ever shows TypeScript
 * never downloads the C# grammar.
 *
 * The unit of output is a **line of HTML** rather than a `<pre>` block: every consumer here
 * (file viewer, diff rows, markdown fences) draws its own gutter, its own row tints and its
 * own scroll container, so what they need is "the tokens of line N" that they can drop into a
 * cell — not shiki's ready-made block.
 */

/** Above this the JS engine gets slow enough to jank the tab; callers fall back to plain text. */
const MAX_HIGHLIGHT_CHARS = 300_000;

// FontStyle bit flags from vscode-textmate — mirrored here rather than imported so the
// module has no runtime dependency beyond shiki/core.
const FONT_ITALIC = 1;
const FONT_BOLD = 2;
const FONT_UNDERLINE = 4;

const themeForeground = (orkkaDark.fg ?? "").toLowerCase();

let highlighterPromise: Promise<HighlighterCore> | null = null;
const loadedLangs = new Set<CodeLang>();
const langLoads = new Map<CodeLang, Promise<void>>();

function getHighlighter(): Promise<HighlighterCore> {
  highlighterPromise ??= createHighlighterCore({
    themes: [orkkaDark],
    langs: [],
    // forgiving: a grammar rule the JS engine can't translate degrades to "no match" for
    // that rule instead of failing the whole file.
    engine: createJavaScriptRegexEngine({ forgiving: true }),
  });
  return highlighterPromise;
}

async function ensureLanguage(lang: CodeLang): Promise<void> {
  if (loadedLangs.has(lang)) return;
  let pending = langLoads.get(lang);
  if (!pending) {
    pending = (async () => {
      const [highlighter, mod] = await Promise.all([getHighlighter(), LANG_LOADERS[lang]()]);
      await highlighter.loadLanguage(...mod.default);
      loadedLangs.add(lang);
    })();
    // A failed grammar load must not poison every later call — drop it so the next
    // request retries (network blips on the chunk are the realistic case).
    pending.catch(() => langLoads.delete(lang));
    langLoads.set(lang, pending);
  }
  await pending;
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function tokenStyle(token: ThemedToken): string | null {
  const parts: string[] = [];
  const color = token.color?.toLowerCase();
  if (color && color !== themeForeground) parts.push(`color:${color}`);
  const fs = token.fontStyle ?? 0;
  if (fs > 0) {
    if (fs & FONT_ITALIC) parts.push("font-style:italic");
    if (fs & FONT_BOLD) parts.push("font-weight:600");
    if (fs & FONT_UNDERLINE) parts.push("text-decoration:underline");
  }
  return parts.length ? parts.join(";") : null;
}

function renderLine(tokens: ThemedToken[]): string {
  let html = "";
  for (const token of tokens) {
    if (!token.content) continue;
    const text = escapeHtml(token.content);
    const style = tokenStyle(token);
    html += style ? `<span style="${style}">${text}</span>` : text;
  }
  return html;
}

/**
 * Highlights `code` and returns one HTML string per line — exactly `code.split("\n").length`
 * entries, so callers can zip it against their own line model. Returns null when the input
 * is too large to highlight; callers then render plain text.
 *
 * Output is safe to inject: token text is escaped, and the only markup is `<span style>`
 * with colours/font styles from the theme.
 */
async function highlightToLines(code: string, lang: CodeLang): Promise<string[] | null> {
  if (code.length > MAX_HIGHLIGHT_CHARS) return null;

  const normalized = code.replace(/\r\n?/g, "\n");
  const expected = normalized.split("\n").length;

  await ensureLanguage(lang);
  const highlighter = await getHighlighter();
  const lines = highlighter
    .codeToTokensBase(normalized, { lang, theme: ORKKA_DARK_THEME_NAME })
    .map(renderLine);

  // Belt and braces: shiki splits on the same newlines we do, but the caller's zip must
  // never go out of step, so pin the length either way.
  if (lines.length > expected) lines.length = expected;
  while (lines.length < expected) lines.push("");
  return lines;
}

/** Plain-text fallback with the same escaping contract as highlightToLines. */
function plainLines(code: string): string[] {
  return code.replace(/\r\n?/g, "\n").split("\n").map(escapeHtml);
}

export { highlightToLines, plainLines, escapeHtml, MAX_HIGHLIGHT_CHARS };
