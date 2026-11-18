import { isValidElement, type ReactNode } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Link } from "@/i18n/navigation";
import { CodeBlock } from "@/components/code/CodeBlock";
import { cn } from "@/lib/utils";

interface MarkdownProps {
  children: string;
  className?: string;
}

/**
 * react-markdown renders a fence as <pre><code class="language-x">text</code></pre>. Pull
 * the language and the raw text back out of that <code> element so the block can go through
 * the shared highlighter; anything else (a <pre> without a single code child) renders as-is.
 */
function fenceFromPreChildren(children: ReactNode): { code: string; lang: string | null } | null {
  if (!isValidElement<{ className?: string; children?: ReactNode }>(children)) return null;
  const { className, children: inner } = children.props;
  const text = Array.isArray(inner) ? inner.join("") : inner;
  if (typeof text !== "string") return null;
  const lang = /language-([^\s]+)/.exec(className ?? "")?.[1] ?? null;
  return { code: text, lang };
}

/**
 * Renders agent-authored markdown (plans, messages) styled for the dark theme.
 * Server-compatible — no client hooks.
 */
function Markdown({ children, className }: MarkdownProps) {
  return (
    <div className={cn("text-sm text-gray-300 leading-relaxed space-y-3 break-words", className)}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          h1: ({ children }) => (
            <h1 className="font-display text-base font-semibold text-white mt-4 first:mt-0">{children}</h1>
          ),
          h2: ({ children }) => (
            <h2 className="font-display text-sm font-semibold text-white mt-4 first:mt-0">{children}</h2>
          ),
          h3: ({ children }) => (
            <h3 className="text-sm font-semibold text-gray-200 mt-3 first:mt-0">{children}</h3>
          ),
          h4: ({ children }) => (
            <h4 className="text-label mt-3 first:mt-0">{children}</h4>
          ),
          p: ({ children }) => <p>{children}</p>,
          ul: ({ children }) => <ul className="list-disc pl-5 space-y-1">{children}</ul>,
          ol: ({ children }) => <ol className="list-decimal pl-5 space-y-1">{children}</ol>,
          li: ({ children }) => <li className="marker:text-gray-500">{children}</li>,
          strong: ({ children }) => <strong className="font-semibold text-gray-100">{children}</strong>,
          a: ({ href, children }) =>
            // In-app routes navigate client-side; everything else opens a new tab
            href?.startsWith("/") ? (
              <Link
                href={href}
                className="text-primary-400 underline underline-offset-2 hover:text-primary-300"
              >
                {children}
              </Link>
            ) : (
              <a
                href={href}
                target="_blank"
                rel="noopener noreferrer"
                className="text-primary-400 underline underline-offset-2 hover:text-primary-300"
              >
                {children}
              </a>
            ),
          code: ({ className: codeClassName, children }) => {
            const isBlock = /language-/.test(codeClassName ?? "");
            return isBlock ? (
              <code className={cn("block font-mono text-xs", codeClassName)}>{children}</code>
            ) : (
              <code className="rounded bg-white/[0.06] px-1.5 py-0.5 font-mono text-xs text-gray-200 break-words">
                {children}
              </code>
            );
          },
          pre: ({ children }) => {
            const fence = fenceFromPreChildren(children);
            return fence ? (
              <CodeBlock code={fence.code} lang={fence.lang} />
            ) : (
              <pre className="rounded-xl bg-black/30 border border-white/[0.05] p-3 overflow-x-auto font-mono text-xs text-gray-300">
                {children}
              </pre>
            );
          },
          blockquote: ({ children }) => (
            <blockquote className="border-l-2 border-white/10 pl-3 text-gray-400 italic">
              {children}
            </blockquote>
          ),
          hr: () => <hr className="border-white/[0.06]" />,
          table: ({ children }) => (
            <div className="overflow-x-auto">
              <table className="w-full text-xs border-collapse">{children}</table>
            </div>
          ),
          th: ({ children }) => (
            <th className="border border-white/[0.08] bg-white/[0.03] px-2 py-1.5 text-left font-semibold text-gray-200">
              {children}
            </th>
          ),
          td: ({ children }) => (
            <td className="border border-white/[0.08] px-2 py-1.5 text-gray-400">{children}</td>
          ),
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  );
}

export { Markdown };
