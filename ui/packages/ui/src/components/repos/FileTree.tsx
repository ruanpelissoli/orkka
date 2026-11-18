import { useState } from "react";
import { useTranslations } from "use-intl";
import type { TreeNode } from "@/lib/types";
import { cn } from "@/lib/utils";

interface FileTreeProps {
  tree: TreeNode[];
  selectedPath: string | null;
  onSelect: (path: string) => void;
}

function FileTree({ tree, selectedPath, onSelect }: FileTreeProps) {
  const t = useTranslations("dashboard.repoMisc.fileTree");
  if (tree.length === 0) {
    return (
      <div className="px-3 py-6 text-center text-sm text-gray-400">
        {t("noFiles")}
      </div>
    );
  }

  return (
    <div className="py-1">
      {tree.map((node) => (
        <FileTreeItem
          key={node.path}
          node={node}
          depth={0}
          selectedPath={selectedPath}
          onSelect={onSelect}
        />
      ))}
    </div>
  );
}

interface FileTreeItemProps {
  node: TreeNode;
  depth: number;
  selectedPath: string | null;
  onSelect: (path: string) => void;
}

function FileTreeItem({ node, depth, selectedPath, onSelect }: FileTreeItemProps) {
  const [expanded, setExpanded] = useState(false);

  const handleClick = () => {
    if (node.isDirectory) {
      setExpanded(!expanded);
    } else {
      onSelect(node.path);
    }
  };

  return (
    <>
      <button
        onClick={handleClick}
        className={cn(
          "flex w-full items-center gap-1.5 px-2 py-1 text-sm transition-colors hover:bg-white/[0.04]",
          !node.isDirectory && selectedPath === node.path && "bg-white/[0.08] text-white",
          !node.isDirectory && selectedPath !== node.path && "text-gray-400",
          node.isDirectory && "text-gray-300"
        )}
        style={{ paddingLeft: `${depth * 16 + 8}px` }}
      >
        {node.isDirectory ? (
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            className={cn(
              "shrink-0 transition-transform",
              expanded && "rotate-90"
            )}
          >
            <path d="M9 6l6 6-6 6" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        ) : (
          <div className="w-3.5 shrink-0" />
        )}

        {node.isDirectory ? (
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="currentColor"
            className="shrink-0 text-primary-400/80"
          >
            {expanded ? (
              <path d="M2 7.5A2.5 2.5 0 014.5 5h4.172a2 2 0 011.414.586l.828.828A2 2 0 0012.328 7H19.5A2.5 2.5 0 0122 9.5v0a.5.5 0 01-.491.592l-1.166.07A5 5 0 0015.5 13H4a2 2 0 01-2-2V7.5z" />
            ) : (
              <path d="M2 7.5A2.5 2.5 0 014.5 5h4.172a2 2 0 011.414.586l.828.828A2 2 0 0012.328 7H19.5A2.5 2.5 0 0122 9.5V17a2.5 2.5 0 01-2.5 2.5h-15A2.5 2.5 0 012 17V7.5z" />
            )}
          </svg>
        ) : (
          <svg
            width="14"
            height="14"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            className="shrink-0 text-gray-400"
          >
            <path d="M14 2H6a2 2 0 00-2 2v16a2 2 0 002 2h12a2 2 0 002-2V8z" strokeLinecap="round" strokeLinejoin="round" />
            <polyline points="14 2 14 8 20 8" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        )}

        <span className="truncate">{node.name}</span>
      </button>

      {node.isDirectory && expanded && node.children.length > 0 && (
        <div>
          {node.children.map((child) => (
            <FileTreeItem
              key={child.path}
              node={child}
              depth={depth + 1}
              selectedPath={selectedPath}
              onSelect={onSelect}
            />
          ))}
        </div>
      )}
    </>
  );
}

export { FileTree };
