import { useMemo } from "react";
import { cn } from "@/lib/utils";
import { plainLines } from "./highlighter";
import { langForAlias, type CodeLang } from "./languages";
import { useHighlightedLines } from "./use-highlighted-lines";

interface CodeBlockProps {
  code: string;
  /** A grammar key, or a loose alias / fence info string ("ts", "c#", "shell"). */
  lang?: CodeLang | string | null;
  showLineNumbers?: boolean;
  className?: string;
}

/**
 * A fenced code block with Orkka's syntax theme — the shared renderer for markdown fences
 * (messages, plans, docs, briefs). Paints plain first and colours in when the grammar is
 * ready; layout is identical in both states so nothing jumps.
 */
function CodeBlock({ code, lang, showLineNumbers = false, className }: CodeBlockProps) {
  const resolvedLang = useMemo(() => langForAlias(lang), [lang]);
  // Fences end with a trailing newline that would render as an empty last row.
  const source = code.endsWith("\n") ? code.slice(0, -1) : code;
  const highlighted = useHighlightedLines(source, resolvedLang);
  const lines = highlighted ?? plainLines(source);

  return (
    <pre
      className={cn(
        "overflow-x-auto rounded-xl border border-white/[0.05] bg-surface-sunken p-3 font-mono text-xs leading-5 text-gray-200",
        className
      )}
    >
      <code className="block min-w-max">
        {lines.map((html, i) => (
          <span key={i} className="flex">
            {showLineNumbers && (
              <span className="w-8 shrink-0 select-none pr-3 text-right text-gray-400">
                {i + 1}
              </span>
            )}
            <span
              className="whitespace-pre"
              // Escaped by the highlighter; the only markup is <span style>.
              dangerouslySetInnerHTML={{ __html: html || " " }}
            />
          </span>
        ))}
      </code>
    </pre>
  );
}

export { CodeBlock };
export type { CodeBlockProps };
