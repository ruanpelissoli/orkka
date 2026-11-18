import { useMemo } from "react";
import { useTranslations } from "use-intl";
import { createPatch } from "diff";
import { DiffHunkView } from "@/components/pr/DiffHunkView";
import { parsePatch } from "@/components/pr/diff-parser";

interface DocumentDiffViewProps {
  /** Content of the older revision (left side of the comparison). */
  oldContent: string;
  /** Content of the newer revision (right side of the comparison). */
  newContent: string;
}

const NOOP = () => {};
const NO_COMMENTS = new Map<never, never>();

/** Line diff between two document revisions, rendered with the PR diff components. */
function DocumentDiffView({ oldContent, newContent }: DocumentDiffViewProps) {
  const t = useTranslations("dashboard.documents");

  const hunks = useMemo(
    () => parsePatch(createPatch("document.md", oldContent, newContent)),
    [oldContent, newContent]
  );

  if (hunks.length === 0) {
    return <p className="px-1 py-4 text-xs text-gray-400">{t("noChanges")}</p>;
  }

  return (
    <div className="overflow-x-auto rounded-xl border border-white/[0.05]">
      {hunks.map((hunk, i) => (
        <DiffHunkView
          key={i}
          hunk={hunk}
          filePath="document.md"
          taskId=""
          readOnly
          commentingLine={null}
          onStartComment={NOOP}
          onCancelComment={NOOP}
          onCommentAdded={NOOP}
          commentsByLine={NO_COMMENTS}
        />
      ))}
    </div>
  );
}

export { DocumentDiffView };
