import { useMemo, useState, useCallback } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";
import type { WorkspaceFile, FileStatus } from "./workspace-model";

interface ChangedFileTreeProps {
  files: WorkspaceFile[];
  selectedPath: string | null;
  /** The file the agent touched last — gets the pulse. */
  activePath: string | null;
  onSelect: (path: string) => void;
  className?: string;
}

interface DirNode {
  kind: "dir";
  name: string;
  path: string;
  children: TreeNode[];
}
interface FileNode {
  kind: "file";
  name: string;
  file: WorkspaceFile;
}
type TreeNode = DirNode | FileNode;

const STATUS_GLYPH: Record<FileStatus, string> = {
  added: "A",
  modified: "M",
  removed: "D",
  renamed: "R",
};

const STATUS_TONE: Record<FileStatus, string> = {
  added: "text-emerald-400",
  modified: "text-amber-400",
  removed: "text-red-400",
  renamed: "text-primary-400",
};

/**
 * Builds a directory tree from flat paths, then collapses single-child directory chains
 * (`src/app/components` → one row) the way editors do, so a five-level-deep file doesn't
 * cost five rows of chevrons.
 */
function buildTree(files: WorkspaceFile[]): TreeNode[] {
  const root: DirNode = { kind: "dir", name: "", path: "", children: [] };
  for (const file of files) {
    const parts = file.path.split("/").filter(Boolean);
    let node = root;
    for (let i = 0; i < parts.length - 1; i++) {
      const name = parts[i];
      let child = node.children.find((c): c is DirNode => c.kind === "dir" && c.name === name);
      if (!child) {
        child = { kind: "dir", name, path: parts.slice(0, i + 1).join("/"), children: [] };
        node.children.push(child);
      }
      node = child;
    }
    node.children.push({ kind: "file", name: parts[parts.length - 1] ?? file.path, file });
  }

  const sortAndCollapse = (dir: DirNode): DirNode => {
    dir.children.sort((a, b) => {
      if (a.kind !== b.kind) return a.kind === "dir" ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
    dir.children = dir.children.map((c) => {
      if (c.kind !== "dir") return c;
      let cur = sortAndCollapse(c);
      while (cur.children.length === 1 && cur.children[0].kind === "dir") {
        const only = cur.children[0] as DirNode;
        cur = { kind: "dir", name: `${cur.name}/${only.name}`, path: only.path, children: only.children };
      }
      return cur;
    });
    return dir;
  };
  return sortAndCollapse(root).children;
}

function ChangedFileTree({ files, selectedPath, activePath, onSelect, className }: ChangedFileTreeProps) {
  const t = useTranslations("dashboard.codeView");
  const tree = useMemo(() => buildTree(files), [files]);
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set());

  const toggle = useCallback((path: string) => {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }, []);

  if (files.length === 0) {
    return (
      <div className={cn("flex h-full items-center justify-center p-4 text-center text-xs text-gray-400", className)}>
        {t("tree.empty")}
      </div>
    );
  }

  const renderNodes = (nodes: TreeNode[], depth: number): React.ReactNode =>
    nodes.map((node) => {
      if (node.kind === "dir") {
        const isCollapsed = collapsed.has(node.path);
        return (
          <div key={`d:${node.path}`}>
            <button
              type="button"
              onClick={() => toggle(node.path)}
              className="flex w-full items-center gap-1 py-1 pr-2 text-left text-xs text-gray-400 hover:bg-white/[0.04] hover:text-gray-200"
              style={{ paddingLeft: 8 + depth * 12 }}
              aria-expanded={!isCollapsed}
            >
              <svg
                width="10"
                height="10"
                viewBox="0 0 16 16"
                fill="none"
                stroke="currentColor"
                strokeWidth="2"
                aria-hidden="true"
                className={cn("shrink-0 transition-transform", isCollapsed && "-rotate-90")}
              >
                <path d="M4 6l4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              <span className="truncate font-mono">{node.name}</span>
            </button>
            {!isCollapsed && renderNodes(node.children, depth + 1)}
          </div>
        );
      }
      const { file } = node;
      const isSelected = file.path === selectedPath;
      const isActive = file.path === activePath;
      const additions = file.additions + file.liveAdditions;
      const deletions = file.deletions + file.liveDeletions;
      return (
        <button
          key={`f:${file.path}`}
          type="button"
          onClick={() => onSelect(file.path)}
          title={file.previousPath ? `${file.previousPath} → ${file.path}` : file.path}
          aria-current={isSelected ? "true" : undefined}
          className={cn(
            "group flex w-full items-center gap-2 border-l-2 py-1 pr-2 text-left text-xs transition-colors",
            isSelected
              ? "border-primary-400/80 bg-primary-500/[0.10] text-gray-100"
              : "border-transparent text-gray-300 hover:bg-white/[0.04] hover:text-gray-100"
          )}
          style={{ paddingLeft: 8 + depth * 12 + 4 }}
        >
          <span
            className={cn("w-3 shrink-0 text-center font-mono text-xs font-bold", STATUS_TONE[file.status])}
            aria-label={file.status}
          >
            {STATUS_GLYPH[file.status]}
          </span>
          <span className={cn("min-w-0 flex-1 truncate font-mono", file.status === "removed" && "line-through opacity-70")}>
            {node.name}
          </span>
          {isActive && (
            <span className="relative flex h-2 w-2 shrink-0" aria-label={t("tree.agentHere")}>
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-cyan-400 opacity-75" />
              <span className="relative inline-flex h-2 w-2 rounded-full bg-cyan-400" />
            </span>
          )}
          {(additions > 0 || deletions > 0) && (
            <span className="shrink-0 font-mono text-xs tabular-nums">
              {additions > 0 && <span className="text-emerald-400">+{additions}</span>}
              {additions > 0 && deletions > 0 && " "}
              {deletions > 0 && <span className="text-red-400">-{deletions}</span>}
            </span>
          )}
        </button>
      );
    });

  return (
    <nav aria-label={t("tree.label")} className={cn("py-1", className)}>
      {renderNodes(tree, 0)}
    </nav>
  );
}

export { ChangedFileTree, buildTree };
