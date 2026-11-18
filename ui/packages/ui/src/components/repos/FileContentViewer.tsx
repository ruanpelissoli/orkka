import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { GlassCard } from "@/components/ui";
import { Skeleton } from "@/components/ui";
import { langForPath, plainLines, useHighlightedLines } from "@/components/code";

interface FileContentViewerProps {
  content: string | null;
  filePath: string | null;
  loading: boolean;
  /** Rendered on the right side of the header bar (e.g. link/expand buttons). */
  actions?: React.ReactNode;
}

function FileContentViewer({ content, filePath, loading, actions }: FileContentViewerProps) {
  const t = useTranslations("dashboard.fileViewer");
  const lang = useMemo(() => langForPath(filePath), [filePath]);
  const highlighted = useHighlightedLines(content, lang);
  const lines = useMemo(
    () => (content === null ? [] : (highlighted ?? plainLines(content))),
    [content, highlighted]
  );

  if (loading) {
    return (
      <GlassCard padding="none" className="flex-1 overflow-hidden">
        <div className="flex items-center justify-between border-b border-white/[0.06] px-4 py-2.5">
          <Skeleton className="h-4 w-48" />
          {actions}
        </div>
        <div className="p-4 space-y-2">
          <Skeleton className="h-3.5 w-full" />
          <Skeleton className="h-3.5 w-4/5" />
          <Skeleton className="h-3.5 w-3/4" />
          <Skeleton className="h-3.5 w-full" />
          <Skeleton className="h-3.5 w-2/3" />
          <Skeleton className="h-3.5 w-5/6" />
          <Skeleton className="h-3.5 w-full" />
          <Skeleton className="h-3.5 w-3/5" />
        </div>
      </GlassCard>
    );
  }

  if (!filePath) {
    return (
      <GlassCard padding="lg" className="flex-1 flex items-center justify-center">
        <div className="text-center text-gray-400">
          <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" className="mx-auto mb-3 opacity-40">
            <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
            <polyline points="14 2 14 8 20 8" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <p className="text-sm">{t("selectFile")}</p>
        </div>
      </GlassCard>
    );
  }

  return (
    <GlassCard padding="none" className="flex-1 overflow-hidden flex flex-col">
      <div className="flex items-center justify-between border-b border-white/[0.06] px-4 py-2.5">
        <span className="text-xs font-mono text-gray-400 truncate">{filePath}</span>
        {actions}
      </div>
      <div className="flex-1 overflow-auto bg-surface-sunken">
        {content === null ? (
          <div className="p-4 text-sm text-gray-400">{t("emptyFile")}</div>
        ) : (
          <pre className="text-sm leading-5">
            <table className="w-full border-collapse">
              <tbody>
                {lines.map((html, i) => (
                  <tr key={i} className="hover:bg-white/[0.02]">
                    <td className="sticky left-0 select-none px-3 py-0 text-right text-gray-400 bg-inherit w-[1%] whitespace-nowrap font-mono text-sm">
                      {i + 1}
                    </td>
                    <td
                      className="px-3 py-0 whitespace-pre font-mono text-gray-200"
                      // Escaped by the highlighter; the only markup is <span style>.
                      dangerouslySetInnerHTML={{ __html: html || " " }}
                    />
                  </tr>
                ))}
              </tbody>
            </table>
          </pre>
        )}
      </div>
    </GlassCard>
  );
}

export { FileContentViewer };
