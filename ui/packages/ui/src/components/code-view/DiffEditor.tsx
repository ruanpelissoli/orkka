import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { cn, formatRelativeTime } from "@/lib/utils";
import { DiffHunkView } from "@/components/pr/DiffHunkView";
import { parsePatch } from "@/components/pr/diff-parser";
import type { DiffHunk, DiffLine } from "@/lib/types";
import type { WorkspaceFile, LiveEdit, FileStatus } from "./workspace-model";

interface DiffEditorProps {
  file: WorkspaceFile | null;
  /** Whether the agent is still running — decides how "live edits" are labelled. */
  agentActive: boolean;
  className?: string;
}

const NO_COMMENTS = new Map<number, never>();
const noop = () => {};

const STATUS_LABEL_KEY: Record<FileStatus, string> = {
  added: "status.added",
  modified: "status.modified",
  removed: "status.removed",
  renamed: "status.renamed",
};

function splitLines(text: string): string[] {
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop();
  return lines;
}

/**
 * A live edit is not a patch — the tool call says "replace this text with that text", with no
 * line numbers. Rendered as a hunk of deletes followed by adds, which is exactly what the
 * agent asked the tool to do; when the baseline catches up (snapshot / working-tree diff) the
 * real hunks take over.
 */
function hunkFromLiveEdit(edit: LiveEdit, header: string): DiffHunk | null {
  const lines: DiffLine[] = [];
  if (edit.kind === "edit") {
    for (const content of splitLines(edit.oldString ?? "")) {
      lines.push({ type: "delete", content, oldLineNumber: null, newLineNumber: null });
    }
    for (const content of splitLines(edit.newString ?? "")) {
      lines.push({ type: "add", content, oldLineNumber: null, newLineNumber: null });
    }
  } else if (edit.kind === "write" && edit.content !== undefined) {
    for (const content of splitLines(edit.content)) {
      lines.push({ type: "add", content, oldLineNumber: null, newLineNumber: null });
    }
  }
  if (lines.length === 0) return null;
  return { oldStart: 0, oldCount: 0, newStart: 0, newCount: 0, header, lines };
}

function DiffEditor({ file, agentActive, className }: DiffEditorProps) {
  const t = useTranslations("dashboard.codeView");

  const hunks = useMemo(() => (file?.patch ? parsePatch(file.patch) : []), [file]);

  const liveHunks = useMemo(() => {
    if (!file) return [];
    return file.liveEdits
      .map((edit, i) => {
        const when = formatRelativeTime(edit.at);
        const header =
          edit.kind === "edit"
            ? t("live.editHeader", { tool: edit.tool, when })
            : edit.kind === "write"
              ? t("live.writeHeader", { tool: edit.tool, when })
              : t("live.dirtyHeader", { tool: edit.tool, when });
        return { key: `${i}-${edit.at}`, edit, hunk: hunkFromLiveEdit(edit, header), header };
      })
      .filter((x) => x.hunk !== null || x.edit.kind === "dirty");
  }, [file, t]);

  if (!file) {
    return (
      <div className={cn("flex h-full items-center justify-center p-8 text-center", className)}>
        <div className="max-w-xs text-gray-400">
          <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" className="mx-auto mb-3 opacity-40" aria-hidden="true">
            <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
            <polyline points="14 2 14 8 20 8" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <p className="text-sm">{t("editor.selectFile")}</p>
        </div>
      </div>
    );
  }

  const additions = file.additions + file.liveAdditions;
  const deletions = file.deletions + file.liveDeletions;

  return (
    <div className={cn("flex h-full min-h-0 flex-col", className)}>
      {/* File header */}
      <div className="flex shrink-0 flex-wrap items-center gap-x-3 gap-y-1 border-b border-white/[0.06] px-4 py-2">
        <span className="min-w-0 flex-1 truncate font-mono text-xs text-gray-200" dir="rtl">
          <bdi>{file.path}</bdi>
        </span>
        <span className="text-xs text-gray-400">{t(STATUS_LABEL_KEY[file.status])}</span>
        {file.previousPath && (
          <span className="truncate font-mono text-xs text-gray-400" title={file.previousPath}>
            ← {file.previousPath}
          </span>
        )}
        <span className="font-mono text-xs tabular-nums">
          <span className="text-emerald-400">+{additions}</span>{" "}
          <span className="text-red-400">-{deletions}</span>
        </span>
      </div>

      <div className="min-h-0 flex-1 overflow-auto bg-surface-sunken">
        {hunks.length === 0 && liveHunks.length === 0 ? (
          <div className="p-6 text-center text-xs text-gray-400">
            {file.status === "removed"
              ? t("editor.removedNoDiff")
              : agentActive
                ? t("editor.awaitingCapture")
                : t("editor.noDiff")}
          </div>
        ) : (
          <>
            {hunks.map((hunk, i) => (
              <DiffHunkView
                key={`h-${i}`}
                hunk={hunk}
                filePath={file.path}
                taskId=""
                readOnly
                commentingLine={null}
                onStartComment={noop}
                onCancelComment={noop}
                onCommentAdded={noop}
                commentsByLine={NO_COMMENTS}
              />
            ))}

            {liveHunks.length > 0 && (
              <div className={cn(hunks.length > 0 && "border-t border-white/[0.06]")}>
                <div className="flex items-center gap-2 px-4 py-2 text-xs text-gray-400">
                  <span className="relative flex h-1.5 w-1.5" aria-hidden="true">
                    {agentActive && (
                      <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-cyan-400 opacity-75" />
                    )}
                    <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-cyan-400" />
                  </span>
                  <span>{t("live.sectionTitle", { count: liveHunks.length })}</span>
                  <span aria-hidden="true">·</span>
                  <span>{agentActive ? t("live.previewHintActive") : t("live.previewHintIdle")}</span>
                </div>
                {liveHunks.map(({ key, edit, hunk, header }) =>
                  hunk ? (
                    <DiffHunkView
                      key={key}
                      hunk={hunk}
                      filePath={file.path}
                      taskId=""
                      readOnly
                      commentingLine={null}
                      onStartComment={noop}
                      onCancelComment={noop}
                      onCommentAdded={noop}
                      commentsByLine={NO_COMMENTS}
                    />
                  ) : (
                    <div
                      key={key}
                      className="border-y border-white/[0.03] bg-cyan-500/[0.04] px-4 py-1.5 font-mono text-xs text-cyan-400/60"
                    >
                      {header}
                      {edit.kind === "dirty" && ` — ${t("live.dirtyNote")}`}
                    </div>
                  )
                )}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

export { DiffEditor };
