import { useCallback, useMemo } from "react";
import { DiffFileCard } from "./DiffFileCard";
import type { PrFile, PrComment } from "@/lib/types";

interface DiffViewerProps {
  files: PrFile[];
  taskId: string;
  comments: PrComment[];
  onCommentAdded: (comment: PrComment) => void;
  onFileVisible?: (filename: string) => void;
  fileRefs: React.MutableRefObject<Map<string, HTMLDivElement>>;
}

function DiffViewer({ files, taskId, comments, onCommentAdded, fileRefs }: DiffViewerProps) {
  // Group comments by file path
  const commentsByFile = useMemo(() => {
    const map = new Map<string, PrComment[]>();
    for (const c of comments) {
      if (!c.path) continue;
      const existing = map.get(c.path) ?? [];
      existing.push(c);
      map.set(c.path, existing);
    }
    return map;
  }, [comments]);

  const setRef = useCallback(
    (filename: string) => (el: HTMLDivElement | null) => {
      if (el) {
        fileRefs.current.set(filename, el);
      } else {
        fileRefs.current.delete(filename);
      }
    },
    [fileRefs]
  );

  return (
    <div className="space-y-3">
      {files.map((file) => (
        <DiffFileCard
          key={file.filename}
          ref={setRef(file.filename)}
          file={file}
          taskId={taskId}
          comments={commentsByFile.get(file.filename) ?? []}
          onCommentAdded={onCommentAdded}
        />
      ))}
    </div>
  );
}

export { DiffViewer };
