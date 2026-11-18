import { useState, useTransition } from "react";
import { useRouter } from "@/i18n/navigation";
import { GlassCard, Badge, Button, ConfirmDialog, useToast } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import {
  approveDesignAsset,
  rejectDesignAsset,
  deleteDesignAsset,
  getDesignAssetSvg,
} from "@/lib/actions";
import { svgToPngBlob, downloadBlob } from "@/lib/svg-export";
import type { DesignAsset } from "@/lib/types";
import { useTranslations } from "use-intl";

const STATUS_COLORS: Record<string, string> = {
  Pending: "#f59e0b",
  Approved: "#10b981",
  Rejected: "#ef4444",
};

interface DesignAssetCardProps {
  asset: DesignAsset;
  projectId: string;
  highlighted?: boolean;
  /** When set, iterate opens the in-page chat drawer instead of navigating to the team page. */
  onIterate?: (assetId: string) => void;
  /** Opens the version-history timeline for this asset's lineage. */
  onHistory?: () => void;
  /** Opens the move-to-folder dialog for this asset's lineage. */
  onMove?: () => void;
}

function DesignAssetCard({ asset, projectId, highlighted, onIterate, onHistory, onMove }: DesignAssetCardProps) {
  const [isPending, startTransition] = useTransition();
  const [rejecting, setRejecting] = useState(false);
  const [feedback, setFeedback] = useState("");
  const [exportingPng, setExportingPng] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const router = useRouter();
  const { toast } = useToast();
  const t = useTranslations("dashboard.design");
  const tFolders = useTranslations("dashboard.folders");
  const statusColor = STATUS_COLORS[asset.status] ?? "#6b7280";

  // SVGs don't work as avatars/uploads on most platforms — rasterize to PNG in the
  // browser (the API returns the markup; conversion never leaves the client).
  async function handleDownloadPng() {
    setExportingPng(true);
    try {
      const svg = await getDesignAssetSvg(projectId, asset.id);
      if (!svg) throw new Error("SVG markup unavailable");
      const blob = await svgToPngBlob(svg);
      downloadBlob(blob, asset.fileName.replace(/\.svg$/i, "") + ".png");
    } catch (error) {
      console.error("PNG export failed", error);
      toast(t("downloadPngFailed"), "error");
    } finally {
      setExportingPng(false);
    }
  }

  function handleApprove() {
    startTransition(() => {
      approveDesignAsset(projectId, asset.id);
    });
  }

  function handleReject() {
    startTransition(async () => {
      await rejectDesignAsset(projectId, asset.id, feedback.trim() || undefined);
      setRejecting(false);
      setFeedback("");
    });
  }

  function handleDelete() {
    setConfirmingDelete(false);
    startTransition(() => {
      deleteDesignAsset(projectId, asset.id);
    });
  }

  function handleIterate() {
    if (onIterate) {
      onIterate(asset.id);
      return;
    }
    // Uploaded assets have no authoring agent — the team page picks the designer.
    if (!asset.agentId) return;
    router.push(
      `/p/${projectId}/team?agent=${asset.agentId}&tab=chat&asset=${asset.id}`
    );
  }

  return (
    <GlassCard
      hover
      accentColor={statusColor}
      padding="md"
      className={`${isPending ? "opacity-50 pointer-events-none" : ""} ${
        highlighted ? "ring-2 ring-primary-400/60" : ""
      }`}
    >
      {/* Preview — signed URL; GIFs animate natively, SVG renders via <img> (no script execution) */}
      <a
        href={asset.url}
        target="_blank"
        rel="noreferrer"
        className="block overflow-hidden rounded-xl bg-[conic-gradient(at_top_left,#1a1a22,#141419)]"
      >
        <img
          src={asset.url}
          alt={asset.name}
          className="h-40 w-full object-contain p-2"
          loading="lazy"
        />
      </a>

      {/* Name + badges */}
      <div className="mt-3 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="font-display text-sm font-semibold text-white truncate">
            {asset.name}
          </h3>
          <p className="mt-0.5 text-xs text-gray-400">
            {asset.agentName
              ? t("byAgent", { agent: asset.agentName })
              : asset.requestedByUserName
                ? t("uploadedBy", { name: asset.requestedByUserName })
                : t("uploaded")}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <Badge size="sm" variant="default">
            {asset.kind.toUpperCase()}
          </Badge>
          {asset.version > 1 && (
            <Badge variant="info" size="sm">
              v{asset.version}
            </Badge>
          )}
        </div>
      </div>

      {/* Status */}
      <div className="mt-2">
        <Badge
          size="sm"
          dot
          dotColor={statusColor}
          style={{
            borderColor: `${statusColor}33`,
            color: statusColor,
            backgroundColor: `${statusColor}1a`,
          }}
        >
          {t(`status${asset.status}`)}
        </Badge>
        {asset.status === "Rejected" && asset.feedback && (
          <p className="mt-1.5 text-xs text-gray-400 line-clamp-2">{asset.feedback}</p>
        )}
      </div>

      {/* Reject feedback input */}
      {rejecting && (
        <div className="mt-3">
          <textarea
            value={feedback}
            onChange={(e) => setFeedback(e.target.value)}
            placeholder={t("rejectFeedbackPlaceholder")}
            rows={2}
            className="w-full rounded-lg border border-white/[0.06] bg-white/[0.03] px-2.5 py-1.5 text-xs text-white placeholder-gray-600 focus:border-primary-500/50 focus:outline-none"
          />
          <div className="mt-1.5 flex gap-1.5">
            <Button size="sm" variant="danger" onClick={handleReject}>
              {t("confirmReject")}
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setRejecting(false)}>
              {t("cancel")}
            </Button>
          </div>
        </div>
      )}

      {/* Actions */}
      <div className="mt-3 flex flex-wrap items-center gap-1.5 border-t border-white/[0.04] pt-3">
        {asset.status === "Pending" && (
          <RequirePermission permission="DesignReview">
            <Button size="sm" onClick={handleApprove}>
              {t("approve")}
            </Button>
            {!rejecting && (
              <Button size="sm" variant="ghost" onClick={() => setRejecting(true)}>
                {t("reject")}
              </Button>
            )}
          </RequirePermission>
        )}
        <Button size="sm" variant="ghost" onClick={handleIterate}>
          {t("iterate")}
        </Button>
        {onHistory && (
          <Button size="sm" variant="ghost" onClick={onHistory}>
            {t("history")}
          </Button>
        )}
        {onMove && (
          <RequirePermission permission="DesignReview">
            <Button size="sm" variant="ghost" onClick={onMove}>
              {tFolders("move")}
            </Button>
          </RequirePermission>
        )}
        {/* Server-signed attachment disposition names the saved file — the download
            attribute is ignored on cross-origin URLs. */}
        <a href={asset.downloadUrl} target="_blank" rel="noreferrer">
          <Button size="sm" variant="ghost">
            {t("download")}
          </Button>
        </a>
        {asset.kind === "Svg" && (
          <Button
            size="sm"
            variant="ghost"
            onClick={handleDownloadPng}
            disabled={exportingPng}
            title={t("downloadPng")}
          >
            PNG
          </Button>
        )}
        <RequirePermission permission="DesignReview">
          <button
            onClick={() => setConfirmingDelete(true)}
            className="ml-auto rounded-lg p-1.5 text-gray-400 transition-colors hover:bg-red-500/10 hover:text-red-400"
            aria-label={t("delete")}
          >
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
              <path d="M3 6h18M8 6V4a2 2 0 012-2h4a2 2 0 012 2v2m3 0v14a2 2 0 01-2 2H7a2 2 0 01-2-2V6h14" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          </button>
        </RequirePermission>
      </div>

      <ConfirmDialog
        open={confirmingDelete}
        title={asset.name}
        message={t("deleteConfirm")}
        confirmLabel={t("delete")}
        cancelLabel={t("cancel")}
        danger
        pending={isPending}
        onConfirm={handleDelete}
        onCancel={() => setConfirmingDelete(false)}
      />
    </GlassCard>
  );
}

export { DesignAssetCard };
