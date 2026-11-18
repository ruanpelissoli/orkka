import { useMemo, useState, useTransition } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Modal, Badge, Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { cn, formatRelativeTime } from "@/lib/utils";
import { restoreDesignAsset } from "@/lib/actions";
import type { DesignAsset } from "@/lib/types";

const STATUS_COLORS: Record<string, string> = {
  Pending: "#f59e0b",
  Approved: "#10b981",
  Rejected: "#ef4444",
};

interface DesignHistoryModalProps {
  /** Asset whose lineage is shown; null closes the modal. */
  asset: DesignAsset | null;
  /** All loaded assets — the lineage is filtered client-side on rootAssetId. */
  assets: DesignAsset[];
  projectId: string;
  onClose: () => void;
}

/** Version timeline for a design asset lineage (all iterations of one root asset). */
function DesignHistoryModal({ asset, assets, projectId, onClose }: DesignHistoryModalProps) {
  const t = useTranslations("dashboard.design");
  const [compareVersion, setCompareVersion] = useState<number | null>(null);
  const [isRestoring, startRestore] = useTransition();

  const lineage = useMemo(
    () =>
      asset
        ? assets
            .filter((a) => a.rootAssetId === asset.rootAssetId)
            .sort((a, b) => b.version - a.version)
        : [],
    [asset, assets]
  );

  const comparePair = useMemo(() => {
    if (compareVersion === null) return null;
    const newer = lineage.find((a) => a.version === compareVersion);
    if (!newer) return null;
    // Compare against the closest older version in the lineage
    const older = lineage.find((a) => a.version < compareVersion);
    return older ? { older, newer } : null;
  }, [compareVersion, lineage]);

  return (
    <Modal open={!!asset} onClose={onClose} className="md:max-w-3xl md:max-h-[85vh]">
      {asset && (
        <div className="flex max-h-[75vh] flex-col gap-4 overflow-y-auto">
          <h2 className="font-display text-base font-semibold text-white">
            {t("historyTitle", { name: asset.name })}
          </h2>

          {/* Side-by-side compare — plain image previews, no visual diffing */}
          {comparePair && (
            <div className="grid grid-cols-2 gap-3">
              {[comparePair.older, comparePair.newer].map((a) => (
                <div key={a.id} className="rounded-xl border border-white/[0.06] bg-white/[0.02] p-2">
                  <p className="mb-1.5 px-1 text-label">
                    v{a.version}
                  </p>
                  <img
                    src={a.url}
                    alt={`${a.name} v${a.version}`}
                    className="h-48 w-full rounded-lg bg-[conic-gradient(at_top_left,#1a1a22,#141419)] object-contain p-2"
                  />
                </div>
              ))}
            </div>
          )}

          {/* Timeline */}
          <div className="space-y-2">
            {lineage.map((a) => {
              const statusColor = STATUS_COLORS[a.status] ?? "#6b7280";
              const hasOlder = lineage.some((other) => other.version < a.version);
              return (
                <div
                  key={a.id}
                  className="flex gap-3 rounded-xl border border-white/[0.05] bg-white/[0.02] p-3"
                >
                  <img
                    src={a.url}
                    alt={`${a.name} v${a.version}`}
                    className="h-16 w-16 shrink-0 rounded-lg bg-white/[0.04] object-contain p-1"
                    loading="lazy"
                  />
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="text-xs font-medium text-gray-200">v{a.version}</span>
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
                        {t(`status${a.status}`)}
                      </Badge>
                      <span className="text-xs text-gray-400">
                        {formatRelativeTime(a.createdAt)}
                      </span>
                    </div>
                    {a.prompt && (
                      <p className="mt-1 text-xs text-gray-400 line-clamp-2" title={a.prompt}>
                        {a.prompt}
                      </p>
                    )}
                    <p className="mt-1 text-xs text-gray-400">
                      {a.agentName
                        ? t("byAgent", { agent: a.agentName })
                        : a.requestedByUserName
                          ? t("uploadedBy", { name: a.requestedByUserName })
                          : t("uploaded")}
                      {a.agentName && a.requestedByUserName && (
                        <> · {t("askedBy", { name: a.requestedByUserName })}</>
                      )}
                    </p>
                    {a.reviewedByUserName && a.status !== "Pending" && (
                      <p className="mt-0.5 text-xs text-gray-400">
                        {t(a.status === "Approved" ? "approvedBy" : "rejectedBy", {
                          name: a.reviewedByUserName,
                        })}
                        {a.reviewedAt && <> · {formatRelativeTime(a.reviewedAt)}</>}
                      </p>
                    )}
                    <div className="mt-1.5 flex flex-wrap items-center gap-2">
                      {a.conversationId && (
                        <Link
                          href={`/p/${projectId}/team?agent=${a.agentId}&tab=chat&c=${a.conversationId}${a.messageId ? `&m=${a.messageId}` : ""}`}
                          className="inline-flex items-center gap-1 text-xs text-primary-400 transition-colors hover:text-primary-300"
                        >
                          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                            <path d="M21 15a2 2 0 01-2 2H7l-4 4V5a2 2 0 012-2h14a2 2 0 012 2z" strokeLinecap="round" strokeLinejoin="round" />
                          </svg>
                          {t("viewInChat")}
                        </Link>
                      )}
                      {hasOlder && (
                        <Button
                          size="sm"
                          variant="ghost"
                          className={cn(
                            "!px-2 !py-0.5 !text-xs",
                            compareVersion === a.version && "!bg-white/[0.08] !text-white"
                          )}
                          onClick={() =>
                            setCompareVersion((v) => (v === a.version ? null : a.version))
                          }
                        >
                          {t("comparePrevious")}
                        </Button>
                      )}
                      {/* Rolling back copies this version's content into a new latest
                          version — non-destructive, so no confirmation step. */}
                      {a.version !== lineage[0]?.version && (
                        <RequirePermission permission="DesignReview">
                          <Button
                            size="sm"
                            variant="ghost"
                            className="!px-2 !py-0.5 !text-xs"
                            disabled={isRestoring}
                            onClick={() =>
                              startRestore(async () => {
                                await restoreDesignAsset(projectId, a.id);
                              })
                            }
                          >
                            {isRestoring ? t("restoringVersion") : t("restoreVersion")}
                          </Button>
                        </RequirePermission>
                      )}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </Modal>
  );
}

export { DesignHistoryModal };
