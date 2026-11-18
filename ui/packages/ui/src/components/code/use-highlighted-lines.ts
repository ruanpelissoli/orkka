import { useEffect, useState } from "react";
import { highlightToLines } from "./highlighter";
import type { CodeLang } from "./languages";

/**
 * Small memo so a diff hunk or file that re-mounts (tab switch, modal reopen, list
 * virtualisation) paints highlighted on the first frame instead of flashing plain. Bounded;
 * insertion order doubles as LRU-ish eviction. Each hook instance also keeps its own result,
 * so eviction under a very large diff never causes re-highlighting of a mounted line.
 */
const CACHE_LIMIT = 256;
const cache = new Map<string, string[]>();

function cacheKey(lang: CodeLang, code: string): string {
  return `${lang} ${code}`;
}

function remember(key: string, lines: string[]): void {
  if (cache.size >= CACHE_LIMIT) {
    const oldest = cache.keys().next().value;
    if (oldest !== undefined) cache.delete(oldest);
  }
  cache.set(key, lines);
}

interface Settled {
  key: string;
  lines: string[];
}

/**
 * Per-line highlighted HTML for `code`, or null while loading / when there is no grammar /
 * when the input is too large. Consumers render `plainLines(code)` for null, so the only
 * visible difference between states is colour — never layout.
 *
 * Passing `lang = null` short-circuits to null synchronously (no work scheduled).
 */
function useHighlightedLines(
  code: string | null | undefined,
  lang: CodeLang | null
): string[] | null {
  const key = code != null && lang ? cacheKey(lang, code) : null;
  const cached = key ? cache.get(key) : undefined;
  const [settled, setSettled] = useState<Settled | null>(null);
  const own = settled && settled.key === key ? settled.lines : undefined;
  const resolved = cached ?? own;

  useEffect(() => {
    if (!key || resolved || code == null || !lang) return;
    let cancelled = false;
    highlightToLines(code, lang)
      .then((lines) => {
        if (cancelled || !lines) return;
        remember(key, lines);
        setSettled({ key, lines });
      })
      .catch(() => {
        // Grammar failed to load or tokenizer threw — stay on plain text.
      });
    return () => {
      cancelled = true;
    };
  }, [key, resolved, code, lang]);

  return resolved ?? null;
}

export { useHighlightedLines };
