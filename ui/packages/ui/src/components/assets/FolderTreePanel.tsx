import { useMemo, useState } from "react";
import { useTranslations } from "use-intl";
import { cn } from "@/lib/utils";

/** Nested node derived from flat folder paths ("a/b" ⇒ a ▸ b). */
interface FolderNode {
  name: string;
  path: string;
  children: FolderNode[];
}

function buildFolderNodes(folders: string[]): FolderNode[] {
  const roots: FolderNode[] = [];
  for (const folder of [...folders].sort()) {
    if (!folder) continue;
    let level = roots;
    let path = "";
    for (const segment of folder.split("/")) {
      path = path ? `${path}/${segment}` : segment;
      let node = level.find((n) => n.name === segment);
      if (!node) {
        node = { name: segment, path, children: [] };
        level.push(node);
      }
      level = node.children;
    }
  }
  return roots;
}

interface FolderTreePanelProps {
  /** All folder paths in use (plus any client-side pending ones). "" is implicit root. */
  folders: string[];
  /** Selected folder path; null = "All items". */
  selected: string | null;
  /** Item count per folder path (exact match), "" for root, used for badges. */
  counts: Record<string, number>;
  totalCount: number;
  onSelect: (folder: string | null) => void;
  /** Renames a folder (bulk path-prefix move on the server). */
  onRename: (fromPath: string, toPath: string) => void;
  /** Registers a client-side folder so items can be moved into it. */
  onCreateFolder: (path: string) => void;
  /**
   * Removes a folder. Optional: a gallery that has nowhere to put the items yet simply does
   * not offer it, rather than showing a button that cannot work. The panel does not confirm —
   * the owner of the items knows how many are in there and what deleting them means.
   */
  onDeleteFolder?: (path: string) => void;
}

/**
 * Folder sidebar for asset galleries (Design assets, Architecture diagrams). Folders are
 * virtual — derived from the items' folderPath values — so an empty folder only exists
 * client-side until an item is moved into it.
 */
export function FolderTreePanel({
  folders,
  selected,
  counts,
  totalCount,
  onSelect,
  onRename,
  onCreateFolder,
  onDeleteFolder,
}: FolderTreePanelProps) {
  const t = useTranslations("dashboard.folders");
  const nodes = useMemo(() => buildFolderNodes(folders), [folders]);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [renaming, setRenaming] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState("");
  const [creating, setCreating] = useState(false);
  const [newFolderValue, setNewFolderValue] = useState("");

  function toggleCollapsed(path: string) {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }

  function submitRename(fromPath: string) {
    const name = renameValue.trim().replace(/[/\\]/g, "-");
    setRenaming(null);
    if (!name || name === fromPath.split("/").pop()) return;
    const parent = fromPath.includes("/") ? fromPath.slice(0, fromPath.lastIndexOf("/")) : "";
    onRename(fromPath, parent ? `${parent}/${name}` : name);
  }

  function submitNewFolder() {
    const name = newFolderValue.trim().replace(/[/\\]/g, "-");
    setCreating(false);
    setNewFolderValue("");
    if (!name) return;
    // New folders nest under the selected folder — matching every file manager.
    const path = selected ? `${selected}/${name}` : name;
    onCreateFolder(path);
    onSelect(path);
  }

  function renderNode(node: FolderNode, depth: number) {
    const isCollapsed = collapsed.has(node.path);
    const isSelected = selected === node.path;
    return (
      <div key={node.path}>
        <div
          className={cn(
            "group flex cursor-pointer items-center gap-1.5 px-2 py-1 text-sm transition-colors hover:bg-white/[0.04]",
            isSelected ? "bg-white/[0.08] text-white" : "text-gray-300"
          )}
          style={{ paddingLeft: `${depth * 16 + 8}px` }}
          onClick={() => onSelect(node.path)}
        >
          <button
            onClick={(e) => {
              e.stopPropagation();
              toggleCollapsed(node.path);
            }}
            className={cn("shrink-0 text-gray-400", node.children.length === 0 && "invisible")}
            aria-label={isCollapsed ? t("expand") : t("collapse")}
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.5"
              className={cn("transition-transform", !isCollapsed && "rotate-90")}
            >
              <path d="M9 6l6 6-6 6" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
          <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor" className="shrink-0 text-primary-400/80">
            {!isCollapsed && node.children.length > 0 ? (
              <path d="M2 7.5A2.5 2.5 0 014.5 5h4.172a2 2 0 011.414.586l.828.828A2 2 0 0012.328 7H19.5A2.5 2.5 0 0122 9.5v0a.5.5 0 01-.491.592l-1.166.07A5 5 0 0015.5 13H4a2 2 0 01-2-2V7.5z" />
            ) : (
              <path d="M2 7.5A2.5 2.5 0 014.5 5h4.172a2 2 0 011.414.586l.828.828A2 2 0 0012.328 7H19.5A2.5 2.5 0 0122 9.5V17a2.5 2.5 0 01-2.5 2.5h-15A2.5 2.5 0 012 17V7.5z" />
            )}
          </svg>
          {renaming === node.path ? (
            <input
              autoFocus
              value={renameValue}
              onChange={(e) => setRenameValue(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") submitRename(node.path);
                if (e.key === "Escape") setRenaming(null);
              }}
              onBlur={() => submitRename(node.path)}
              onClick={(e) => e.stopPropagation()}
              className="min-w-0 flex-1 rounded border border-primary-500/50 bg-black/40 px-1 py-0.5 text-sm text-white outline-none"
            />
          ) : (
            <>
              <span className="min-w-0 flex-1 truncate">{node.name}</span>
              {counts[node.path] ? (
                <span className="shrink-0 text-xs text-gray-400">{counts[node.path]}</span>
              ) : null}
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  setRenameValue(node.name);
                  setRenaming(node.path);
                }}
                className="shrink-0 rounded p-0.5 text-gray-400 opacity-0 transition-opacity hover:text-gray-300 group-hover:opacity-100"
                aria-label={t("renameFolder")}
                title={t("renameFolder")}
              >
                <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                  <path d="M17 3a2.83 2.83 0 114 4L7.5 20.5 2 22l1.5-5.5z" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </button>
              {onDeleteFolder && (
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    onDeleteFolder(node.path);
                  }}
                  className="shrink-0 rounded p-0.5 text-gray-400 opacity-0 transition-opacity hover:text-red-400 group-hover:opacity-100"
                  aria-label={t("deleteFolder")}
                  title={t("deleteFolder")}
                >
                  <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    <path d="M3 6h18M8 6V4h8v2m-9 0v14a2 2 0 002 2h6a2 2 0 002-2V6" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </button>
              )}
            </>
          )}
        </div>
        {!isCollapsed && node.children.map((child) => renderNode(child, depth + 1))}
      </div>
    );
  }

  return (
    <div className="flex min-w-0 flex-col py-1">
      <div className="mb-1 flex items-center justify-between px-2">
        <span className="text-label">
          {t("folders")}
        </span>
        <button
          onClick={() => setCreating(true)}
          className="rounded-lg p-1 text-gray-400 transition-colors hover:bg-white/[0.06] hover:text-gray-300"
          aria-label={t("newFolder")}
          title={t("newFolder")}
        >
          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
            <path d="M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z" strokeLinecap="round" strokeLinejoin="round" />
            <path d="M12 11v6m-3-3h6" strokeLinecap="round" />
          </svg>
        </button>
      </div>

      <button
        onClick={() => onSelect(null)}
        className={cn(
          "flex items-center gap-1.5 px-2 py-1 text-left text-sm transition-colors hover:bg-white/[0.04]",
          selected === null ? "bg-white/[0.08] text-white" : "text-gray-300"
        )}
      >
        <span className="min-w-0 flex-1 truncate">{t("allItems")}</span>
        <span className="shrink-0 text-xs text-gray-400">{totalCount}</span>
      </button>

      {nodes.map((node) => renderNode(node, 0))}

      {creating && (
        <input
          autoFocus
          value={newFolderValue}
          onChange={(e) => setNewFolderValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") submitNewFolder();
            if (e.key === "Escape") {
              setCreating(false);
              setNewFolderValue("");
            }
          }}
          onBlur={submitNewFolder}
          placeholder={t("folderName")}
          className="mx-2 mt-1 rounded-lg border border-primary-500/50 bg-black/40 px-2 py-1 text-sm text-white placeholder-gray-600 outline-none"
        />
      )}
    </div>
  );
}

interface MoveToFolderDialogProps {
  open: boolean;
  folders: string[];
  /** The item's current folder ("" = root) — shown disabled. */
  current: string;
  onClose: () => void;
  onMove: (folderPath: string) => void;
}

/** Folder picker for moving one item; includes root and an inline "new folder" input. */
export function MoveToFolderDialog({ open, folders, current, onClose, onMove }: MoveToFolderDialogProps) {
  const t = useTranslations("dashboard.folders");
  const [newFolder, setNewFolder] = useState("");
  if (!open) return null;

  const options = ["", ...[...new Set(folders.filter(Boolean))].sort()];

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" onClick={onClose}>
      <div
        className="w-full max-w-sm rounded-2xl border border-white/[0.08] bg-surface-overlay p-4 shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <h3 className="mb-3 font-display text-sm font-semibold text-white">{t("moveToFolder")}</h3>
        <div className="max-h-64 space-y-0.5 overflow-y-auto">
          {options.map((folder) => (
            <button
              key={folder || "__root"}
              disabled={folder === current}
              onClick={() => onMove(folder)}
              className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs text-gray-300 transition-colors hover:bg-white/[0.05] disabled:opacity-40"
            >
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" className="shrink-0 text-amber-400/70">
                <path d="M22 19a2 2 0 01-2 2H4a2 2 0 01-2-2V5a2 2 0 012-2h5l2 3h9a2 2 0 012 2z" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              <span className="min-w-0 truncate">{folder || t("root")}</span>
            </button>
          ))}
        </div>
        <div className="mt-3 flex gap-1.5 border-t border-white/[0.05] pt-3">
          <input
            value={newFolder}
            onChange={(e) => setNewFolder(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && newFolder.trim()) onMove(newFolder.trim());
            }}
            placeholder={t("newFolderPlaceholder")}
            className="min-w-0 flex-1 rounded-lg border border-white/[0.08] bg-white/[0.03] px-2 py-1.5 text-xs text-white placeholder-gray-600 outline-none focus:border-primary-500/50"
          />
          <button
            disabled={!newFolder.trim()}
            onClick={() => onMove(newFolder.trim())}
            className="shrink-0 rounded-lg bg-primary-600 px-3 py-1.5 text-xs font-medium text-white transition-colors hover:bg-primary-500 disabled:opacity-40"
          >
            {t("move")}
          </button>
        </div>
      </div>
    </div>
  );
}
