import { useMemo, useRef, useState } from "react";
import { useSearchParams } from "@/lib/navigation";
import { Link } from "@/i18n/navigation";
import { Button, ConfirmDialog, EmptyState, GlassCard, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { DesignAssetCard } from "./DesignAssetCard";
import { DesignHistoryModal } from "./DesignHistoryModal";
import { AgentChatDrawer, type AttachRequest } from "../team/AgentChatDrawer";
import { FolderTreePanel, MoveToFolderDialog } from "@/components/assets/FolderTreePanel";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { deleteDesignFolder, moveDesignAsset, renameDesignFolder } from "@/lib/actions";
import type { DesignAsset, DesignAssetStatus } from "@/lib/types";
import { useTranslations } from "use-intl";
import { refreshData } from "@/lib/data-refresh";

const MAX_UPLOAD_BYTES = 10 * 1024 * 1024;
const UPLOAD_ACCEPT = ".png,.jpg,.jpeg,.webp,.gif,.svg";

type StatusFilter = "All" | DesignAssetStatus;

const FILTERS: StatusFilter[] = ["All", "Pending", "Approved", "Rejected"];

interface DesignGalleryProps {
  assets: DesignAsset[];
  projectId: string;
}

function DesignGallery({ assets, projectId }: DesignGalleryProps) {
  const searchParams = useSearchParams();
  // Deep link from agent chat: /design?a={assetId} highlights the asset
  const highlightedId = searchParams.get("a");
  const [filter, setFilter] = useState<StatusFilter>("All");
  // Iterate opens the side chat with Muse and the asset attached — a fresh object per
  // click so re-iterating the same asset after closing the drawer reopens it.
  const [iterateRequest, setIterateRequest] = useState<AttachRequest | null>(null);
  const [historyAsset, setHistoryAsset] = useState<DesignAsset | null>(null);
  // Folder organization: null = all items; "" = root. Folders are virtual (derived from
  // folderPath values), so freshly created empty ones live client-side until used.
  const [selectedFolder, setSelectedFolder] = useState<string | null>(null);
  const [pendingFolders, setPendingFolders] = useState<string[]>([]);
  // Folders assets have just been moved into, keyed by lineage, applied over the server's
  // copy until the refreshed page arrives.
  const [movedFolders, setMovedFolders] = useState<Record<string, string>>({});
  // The folder awaiting confirmation, with what deleting it costs — an empty folder is only
  // a client-side path, so it goes without ceremony.
  const [folderToDelete, setFolderToDelete] = useState<{ path: string; count: number } | null>(
    null
  );
  const [moveAsset, setMoveAsset] = useState<DesignAsset | null>(null);
  const [isUploading, setIsUploading] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const withToken = useClientApi();
  const { toast } = useToast();
  const t = useTranslations("dashboard.design");

  // Removing a folder also drops it from the client-side list, which is the only place an
  // empty one ever existed, and moves the selection off a folder that is now gone.
  const removeFolder = async (path: string, deleteContents: boolean) => {
    setFolderToDelete(null);
    try {
      await deleteDesignFolder(projectId, path, deleteContents);
      setPendingFolders((prev) =>
        prev.filter((f) => f !== path && !f.startsWith(path + "/"))
      );
      if (selectedFolder === path || selectedFolder?.startsWith(path + "/")) {
        setSelectedFolder(null);
      }
      refreshData();
    } catch {
      toast(t("folderDeleteFailed"), "error");
    }
  };

  // Uploads land in the folder currently selected in the tree (all/root → root).
  const handleUploadFiles = async (files: FileList | null) => {
    if (!files || files.length === 0) return;
    setIsUploading(true);
    const targetFolder = selectedFolder ?? "";
    for (const file of Array.from(files)) {
      if (file.size > MAX_UPLOAD_BYTES) {
        toast(t("uploadTooLarge", { name: file.name }), "error");
        continue;
      }
      try {
        await withToken((token) => api.designAssets.upload(projectId, file, targetFolder, token));
      } catch {
        toast(t("uploadFailed", { name: file.name }), "error");
      }
    }
    setIsUploading(false);
    refreshData();
  };

  // One card per lineage: iterating on an asset REPLACES its card with the new version
  // instead of adding a sibling. Older versions stay in `assets` for the history modal.
  const latestPerLineage = useMemo(() => {
    const byRoot = new Map<string, DesignAsset>();
    for (const a of assets) {
      const current = byRoot.get(a.rootAssetId);
      if (!current || a.version > current.version) byRoot.set(a.rootAssetId, a);
    }
    // A just-moved asset is shown in its new folder before the server's copy catches up;
    // once the refreshed props say the same thing, the override is a no-op.
    return [...byRoot.values()].map((a) =>
      a.rootAssetId in movedFolders ? { ...a, folderPath: movedFolders[a.rootAssetId] } : a
    );
  }, [assets, movedFolders]);

  // A chat deep-link may target an older version of a lineage — highlight its latest card.
  const highlightedRoot = useMemo(
    () => (highlightedId ? (assets.find((a) => a.id === highlightedId)?.rootAssetId ?? null) : null),
    [assets, highlightedId]
  );

  const folders = useMemo(
    () => [...new Set([...latestPerLineage.map((a) => a.folderPath).filter(Boolean), ...pendingFolders])],
    [latestPerLineage, pendingFolders]
  );

  const folderCounts = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const asset of latestPerLineage)
      counts[asset.folderPath] = (counts[asset.folderPath] ?? 0) + 1;
    return counts;
  }, [latestPerLineage]);

  const filtered = useMemo(() => {
    let items = filter === "All" ? latestPerLineage : latestPerLineage.filter((a) => a.status === filter);
    if (selectedFolder !== null) {
      // A folder shows its own items plus everything nested below it.
      items = items.filter(
        (a) => a.folderPath === selectedFolder || (selectedFolder !== "" && a.folderPath.startsWith(selectedFolder + "/"))
      );
    }
    return items;
  }, [latestPerLineage, filter, selectedFolder]);

  const counts = useMemo(() => {
    const byStatus: Record<StatusFilter, number> = {
      All: latestPerLineage.length,
      Pending: 0,
      Approved: 0,
      Rejected: 0,
    };
    for (const asset of latestPerLineage) byStatus[asset.status]++;
    return byStatus;
  }, [latestPerLineage]);

  return (
    <>
      {/* Header */}
      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <h1 className="font-display text-xl font-bold text-white">{t("title")}</h1>

        <div className="flex flex-wrap items-center gap-2">
          {assets.length > 0 && (
            <div className="flex items-center gap-1 rounded-xl bg-white/[0.03] p-1">
              {FILTERS.map((status) => (
                <button
                  key={status}
                  onClick={() => setFilter(status)}
                  className={`rounded-lg px-3 py-1.5 text-xs font-medium transition-colors ${
                    filter === status
                      ? "bg-white/[0.08] text-white"
                      : "text-gray-400 hover:text-gray-200"
                  }`}
                >
                  {t(`filter${status}`)}
                  <span className="ml-1.5 text-xs text-gray-400">
                    {counts[status]}
                  </span>
                </button>
              ))}
            </div>
          )}
          <RequirePermission permission="DesignReview">
            <input
              ref={fileInputRef}
              type="file"
              accept={UPLOAD_ACCEPT}
              multiple
              className="hidden"
              onChange={(e) => {
                handleUploadFiles(e.target.files);
                e.target.value = "";
              }}
            />
            <Button
              size="sm"
              onClick={() => fileInputRef.current?.click()}
              disabled={isUploading}
            >
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <path d="M12 16V4m0 0L7 9m5-5l5 5M4 20h16" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
              {isUploading ? t("uploading") : t("upload")}
            </Button>
          </RequirePermission>
        </div>
      </div>

      <div className="flex min-h-0 flex-1 flex-col gap-4 lg:flex-row">
        {/* Folder pane — compact scrollable strip above the gallery on mobile */}
        <GlassCard className="max-h-44 w-full shrink-0 overflow-y-auto !p-2 lg:max-h-none lg:w-64">
        <FolderTreePanel
          folders={folders}
          selected={selectedFolder}
          counts={folderCounts}
          totalCount={latestPerLineage.length}
          onSelect={setSelectedFolder}
          onRename={async (from, to) => {
            try {
              await renameDesignFolder(projectId, from, to);
              setPendingFolders((prev) => prev.map((f) => (f === from ? to : f)));
              if (selectedFolder === from) setSelectedFolder(to);
            } catch {
              /* revalidation restores server truth */
            }
          }}
          onCreateFolder={(path) =>
            setPendingFolders((prev) => (prev.includes(path) ? prev : [...prev, path]))
          }
          onDeleteFolder={(path) => {
            // Everything at or below the folder, which is what the server will delete.
            const count = latestPerLineage.filter(
              (a) => a.folderPath === path || a.folderPath.startsWith(path + "/")
            ).length;
            if (count === 0) {
              void removeFolder(path, false);
              return;
            }
            setFolderToDelete({ path, count });
          }}
        />
        </GlassCard>
        <div className="min-w-0 flex-1 overflow-y-auto">
      {/* Grid or Empty State */}
      {filtered.length === 0 ? (
        <EmptyState
          title={assets.length === 0 ? t("noAssets") : t("noAssetsFiltered")}
          description={t("noAssetsDescription")}
          icon={
            <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
              <rect x="3" y="3" width="18" height="18" rx="2" strokeLinecap="round" />
              <circle cx="9" cy="9" r="2" />
              <path d="M21 15l-3.5-3.5a1.5 1.5 0 00-2.1 0L7 20" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          }
          action={
            assets.length === 0 ? (
              <Link href={`/p/${projectId}/team`}>
                <Button size="sm">{t("goToTeam")}</Button>
              </Link>
            ) : undefined
          }
        />
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
          {filtered.map((asset) => (
            <DesignAssetCard
              key={asset.id}
              asset={asset}
              projectId={projectId}
              highlighted={asset.rootAssetId === highlightedRoot}
              onIterate={(assetId) => setIterateRequest({ assetId })}
              onHistory={() => setHistoryAsset(asset)}
              onMove={() => setMoveAsset(asset)}
            />
          ))}
        </div>
      )}
        </div>
      </div>

      {/* Move an asset lineage into a folder */}
      <MoveToFolderDialog
        open={moveAsset !== null}
        folders={folders}
        current={moveAsset?.folderPath ?? ""}
        onClose={() => setMoveAsset(null)}
        onMove={async (folderPath) => {
          const asset = moveAsset;
          setMoveAsset(null);
          if (!asset) return;
          // Move the card out of the current folder NOW. The server call and the refresh
          // that follows take a moment, and until this was here the asset sat in the folder
          // it had just left, looking like the move had not happened.
          setMovedFolders((prev) => ({ ...prev, [asset.rootAssetId]: folderPath }));
          try {
            await moveDesignAsset(projectId, asset.id, folderPath);
            refreshData();
          } catch {
            // Put it back: the move did not happen, and pretending otherwise is worse than
            // the flicker.
            setMovedFolders((prev) => {
              const next = { ...prev };
              delete next[asset.rootAssetId];
              return next;
            });
          }
        }}
      />

      <ConfirmDialog
        open={folderToDelete !== null}
        title={t("folderDeleteTitle")}
        message={t("folderDeleteConfirm", {
          folder: folderToDelete?.path ?? "",
          count: folderToDelete?.count ?? 0,
        })}
        confirmLabel={t("folderDeleteConfirmLabel")}
        cancelLabel={t("cancel")}
        danger
        onConfirm={() => {
          if (folderToDelete) void removeFolder(folderToDelete.path, true);
        }}
        onCancel={() => setFolderToDelete(null)}
      />

      {/* Side chat with the Designer — iterate on assets without leaving the gallery */}
      {/* Muse, and Sprite when enabled — the drawer shows a switcher with both */}
      <AgentChatDrawer
        projectId={projectId}
        role={["Designer", "GameDesigner"]}
        attach={iterateRequest}
        // "All items" is not a folder — only a real selection files new assets somewhere.
        designFolderPath={selectedFolder}
      />

      {/* Version timeline for one asset lineage */}
      <DesignHistoryModal
        asset={historyAsset}
        assets={assets}
        projectId={projectId}
        onClose={() => setHistoryAsset(null)}
      />
    </>
  );
}

export { DesignGallery };
