import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { escapeHtml, langForPath, useHighlightedLines } from "@/components/code";
import { InlineCommentForm } from "./InlineCommentForm";
import { PrCommentThread } from "./PrCommentThread";
import type { DiffHunk, DiffLine, PrComment } from "@/lib/types";

/**
 * Syntax colour for a hunk. A grammar needs continuous source to tokenize sensibly, and a
 * hunk is really two overlapping sources — the old side (context + deleted lines) and the
 * new side (context + added lines). Each side is highlighted as one string so tokens carry
 * across lines (multi-line strings, JSX, comments), then zipped back onto the hunk's lines.
 * Context lines take the new side's colours; the two agree in practice and it avoids
 * highlighting them twice.
 *
 * Returns per-line HTML, or null entries while grammars load (callers escape the raw text).
 */
function useHunkHighlight(hunk: DiffHunk, filePath: string): (string | null)[] {
  const lang = useMemo(() => langForPath(filePath), [filePath]);
  const { oldText, newText } = useMemo(() => {
    const oldLines: string[] = [];
    const newLines: string[] = [];
    for (const line of hunk.lines) {
      if (line.type !== "add") oldLines.push(line.content);
      if (line.type !== "delete") newLines.push(line.content);
    }
    return { oldText: oldLines.join("\n"), newText: newLines.join("\n") };
  }, [hunk]);

  const oldHtml = useHighlightedLines(oldText, lang);
  const newHtml = useHighlightedLines(newText, lang);

  return useMemo(() => {
    let o = 0;
    let n = 0;
    return hunk.lines.map((line) => {
      if (line.type === "delete") return oldHtml?.[o++] ?? null;
      if (line.type === "add") return newHtml?.[n++] ?? null;
      o++;
      return newHtml?.[n++] ?? null;
    });
  }, [hunk, oldHtml, newHtml]);
}

interface DiffHunkViewProps {
  hunk: DiffHunk;
  filePath: string;
  taskId: string;
  /** Hides the comment gutter and inline comment forms. */
  readOnly?: boolean;
  commentingLine: number | null;
  onStartComment: (line: number) => void;
  onCancelComment: () => void;
  onCommentAdded: (comment: PrComment) => void;
  commentsByLine: Map<number, PrComment[]>;
}

function DiffHunkView({
  hunk,
  filePath,
  taskId,
  readOnly = false,
  commentingLine,
  onStartComment,
  onCancelComment,
  onCommentAdded,
  commentsByLine,
}: DiffHunkViewProps) {
  const highlighted = useHunkHighlight(hunk, filePath);

  return (
    <div>
      {/* Hunk header */}
      <div className="bg-cyan-500/[0.04] px-4 py-1.5 text-xs font-mono text-cyan-400/60 select-none border-y border-white/[0.03]">
        {hunk.header}
      </div>

      {/* Diff lines */}
      <div className="font-mono text-xs">
        {hunk.lines.map((line, i) => {
          const lineNumber = line.newLineNumber ?? line.oldLineNumber ?? 0;
          const lineComments = line.newLineNumber
            ? commentsByLine.get(line.newLineNumber) ?? []
            : [];

          return (
            <div key={i}>
              <DiffLineRow
                line={line}
                html={highlighted[i]}
                readOnly={readOnly}
                onAddComment={() => onStartComment(lineNumber)}
              />

              {/* Existing comments on this line */}
              {lineComments.length > 0 && (
                <PrCommentThread comments={lineComments} />
              )}

              {/* Inline comment form */}
              {!readOnly && commentingLine === lineNumber && (
                <InlineCommentForm
                  taskId={taskId}
                  path={filePath}
                  line={lineNumber}
                  onCancel={onCancelComment}
                  onCommentAdded={onCommentAdded}
                />
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function DiffLineRow({
  line,
  html,
  readOnly,
  onAddComment,
}: {
  line: DiffLine;
  /** Highlighted HTML for the line, or null to render the raw text. */
  html: string | null;
  readOnly?: boolean;
  onAddComment: () => void;
}) {
  const t = useTranslations("dashboard.prReview");
  return (
    <div
      className={cn(
        "group flex hover:bg-white/[0.02] select-none",
        // Add/delete are row tints only — the token colours stay, which is what keeps a
        // changed line readable instead of a flat block of green or red text.
        line.type === "add" && "bg-emerald-500/[0.10]",
        line.type === "delete" && "bg-red-500/[0.10]"
      )}
    >
      {/* Add comment button (gutter) */}
      {readOnly ? (
        <span className="w-5 shrink-0" />
      ) : (
        <RequirePermission
          permission="CodeReviewReview"
          fallback={<span className="w-5 shrink-0" />}
        >
          <button
            onClick={onAddComment}
            className="w-5 shrink-0 flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity text-cyan-400 hover:text-cyan-300"
            title={t("addComment")}
          >
            <svg width="12" height="12" viewBox="0 0 16 16" fill="none">
              <path d="M8 4v8M4 8h8" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
            </svg>
          </button>
        </RequirePermission>
      )}

      {/* Old line number */}
      <span
        className={cn(
          "w-10 shrink-0 text-right pr-2 select-none text-xs leading-5",
          line.type === "delete" ? "text-red-400/40" : "text-gray-400"
        )}
      >
        {line.oldLineNumber ?? ""}
      </span>

      {/* New line number */}
      <span
        className={cn(
          "w-10 shrink-0 text-right pr-2 select-none text-xs leading-5",
          line.type === "add" ? "text-emerald-400/40" : "text-gray-400"
        )}
      >
        {line.newLineNumber ?? ""}
      </span>

      {/* Prefix */}
      <span
        className={cn(
          "w-4 shrink-0 text-center select-none leading-5",
          line.type === "add" && "text-emerald-400",
          line.type === "delete" && "text-red-400",
          line.type === "context" && "text-gray-400"
        )}
      >
        {line.type === "add" ? "+" : line.type === "delete" ? "-" : " "}
      </span>

      {/* Content — token colours from the shared theme; plain (escaped) text until loaded */}
      <span
        className={cn(
          "flex-1 min-w-0 whitespace-pre overflow-x-auto leading-5 pr-4 text-gray-200",
          line.type === "context" && "opacity-80"
        )}
        dangerouslySetInnerHTML={{ __html: html ?? escapeHtml(line.content) }}
      />
    </div>
  );
}

export { DiffHunkView };
