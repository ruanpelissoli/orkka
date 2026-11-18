import { useState, useMemo, forwardRef } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import { Collapsible } from "@/components/ui";
import { parsePatch } from "./diff-parser";
import { DiffHunkView } from "./DiffHunkView";
import type { PrFile, PrComment } from "@/lib/types";

interface DiffFileCardProps {
  file: PrFile;
  /** Required for inline commenting; omit (with readOnly) for plain diff rendering. */
  taskId?: string;
  comments?: PrComment[];
  onCommentAdded?: (comment: PrComment) => void;
  /** Hides the comment gutter and inline comment forms. */
  readOnly?: boolean;
}

const STATUS_LABELS: Record<string, { labelKey: string; className: string }> = {
  added: { labelKey: "statusAdded", className: "text-emerald-400" },
  modified: { labelKey: "statusModified", className: "text-amber-400" },
  removed: { labelKey: "statusDeleted", className: "text-red-400" },
  renamed: { labelKey: "statusRenamed", className: "text-primary-400" },
  copied: { labelKey: "statusCopied", className: "text-violet-400" },
};

const DiffFileCard = forwardRef<HTMLDivElement, DiffFileCardProps>(
  function DiffFileCard({ file, taskId, comments = [], onCommentAdded, readOnly }, ref) {
    const t = useTranslations("dashboard.prReview");
    const isReadOnly = readOnly ?? !taskId;
    const [collapsed, setCollapsed] = useState(false);
    const [commentingLine, setCommentingLine] = useState<number | null>(null);

    const hunks = useMemo(() => parsePatch(file.patch ?? ""), [file.patch]);
    const status = STATUS_LABELS[file.status] ?? STATUS_LABELS.modified;

    // Group comments by line number for this file
    const commentsByLine = useMemo(() => {
      const map = new Map<number, PrComment[]>();
      for (const c of comments) {
        if (c.line == null) continue;
        const existing = map.get(c.line) ?? [];
        existing.push(c);
        map.set(c.line, existing);
      }
      return map;
    }, [comments]);

    return (
      <div ref={ref} data-filename={file.filename} className="rounded-xl border border-white/[0.06] overflow-hidden">
        {/* File header */}
        <button
          onClick={() => setCollapsed((prev) => !prev)}
          className="flex w-full items-center gap-3 bg-white/[0.02] px-4 py-2.5 text-left hover:bg-white/[0.04] transition-colors sticky top-0 z-10"
        >
          <svg
            width="12"
            height="12"
            viewBox="0 0 16 16"
            fill="none"
            className={cn(
              "shrink-0 text-gray-400 transition-transform",
              collapsed && "-rotate-90"
            )}
          >
            <path d="M4 6l4 4 4-4" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
          </svg>

          <span className="font-mono text-xs text-gray-300 truncate flex-1 min-w-0">
            {file.previousFilename && (
              <span className="text-gray-400">{file.previousFilename} → </span>
            )}
            {file.filename}
          </span>

          <span className={cn("text-xs font-semibold", status.className)}>
            {t(status.labelKey)}
          </span>

          <span className="flex items-center gap-1.5 font-mono text-xs shrink-0">
            {file.additions > 0 && (
              <span className="text-emerald-400">+{file.additions}</span>
            )}
            {file.deletions > 0 && (
              <span className="text-red-400">-{file.deletions}</span>
            )}
          </span>
        </button>

        {/* Diff content */}
        <Collapsible open={!collapsed}>
          <div className="overflow-x-auto">
            {hunks.length > 0 ? (
              hunks.map((hunk, i) => (
                <DiffHunkView
                  key={i}
                  hunk={hunk}
                  filePath={file.filename}
                  taskId={taskId ?? ""}
                  readOnly={isReadOnly}
                  commentingLine={commentingLine}
                  onStartComment={setCommentingLine}
                  onCancelComment={() => setCommentingLine(null)}
                  onCommentAdded={(comment) => {
                    onCommentAdded?.(comment);
                    setCommentingLine(null);
                  }}
                  commentsByLine={commentsByLine}
                />
              ))
            ) : (
              <div className="px-4 py-6 text-center text-xs text-gray-400">
                {t("binaryFile")}
              </div>
            )}
          </div>
        </Collapsible>
      </div>
    );
  }
);

export { DiffFileCard };
