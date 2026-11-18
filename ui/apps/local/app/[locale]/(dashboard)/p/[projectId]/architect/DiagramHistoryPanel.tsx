import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "use-intl";
import { Link } from "@/i18n/navigation";
import { Button } from "@/components/ui";
import { RequirePermission } from "@/components/permissions/RequirePermission";
import { api } from "@/lib/api";
import { useClientApi } from "@/hooks/use-client-api";
import { restoreDiagramVersion } from "@/lib/actions";
import { cn, formatRelativeTime } from "@/lib/utils";
import type { ArchitectureDiagram, DiagramVersionDetail, DiagramVersionMeta } from "@/lib/types";

interface DiagramHistoryPanelProps {
  projectId: string;
  diagramId: string;
  currentVersion: number;
  /** Version currently shown in the side-by-side preview, if any. */
  previewVersion: number | null;
  onPreview: (version: DiagramVersionDetail | null) => void;
  onRestored: (diagram: ArchitectureDiagram) => void;
}

function DiagramHistoryPanel({
  projectId,
  diagramId,
  currentVersion,
  previewVersion,
  onPreview,
  onRestored,
}: DiagramHistoryPanelProps) {
  const t = useTranslations("dashboard.architect");
  const withToken = useClientApi();
  const [versions, setVersions] = useState<DiagramVersionMeta[]>([]);
  const [isRestoring, setIsRestoring] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    withToken((token) => api.diagrams.listVersions(projectId, diagramId, token))
      .then((list) => {
        if (!cancelled) setVersions(list);
      })
      .catch(() => {
        if (!cancelled) setVersions([]);
      });
    return () => {
      cancelled = true;
    };
  }, [projectId, diagramId, currentVersion, withToken]);

  const handleSelect = useCallback(
    async (version: number) => {
      if (previewVersion === version) {
        onPreview(null);
        return;
      }
      try {
        const detail = await withToken((token) =>
          api.diagrams.getVersion(projectId, diagramId, version, token)
        );
        onPreview(detail);
      } catch {
        setError(t("historyLoadFailed"));
      }
    },
    [previewVersion, projectId, diagramId, withToken, onPreview, t]
  );

  const handleRestore = useCallback(
    async (version: number) => {
      if (isRestoring) return;
      setIsRestoring(true);
      setError(null);
      try {
        const diagram = await restoreDiagramVersion(projectId, diagramId, version);
        onPreview(null);
        onRestored(diagram);
      } catch {
        setError(t("restoreFailed"));
      } finally {
        setIsRestoring(false);
      }
    },
    [isRestoring, projectId, diagramId, onPreview, onRestored, t]
  );

  return (
    <div className="flex w-64 shrink-0 flex-col overflow-y-auto border-l border-white/[0.06] p-3">
      <p className="mb-2 px-1 text-label">
        {t("history")}
      </p>
      {error && (
        <p className="mb-2 px-1 text-xs text-red-400">{error}</p>
      )}
      {versions.length === 0 ? (
        <p className="px-1 text-xs text-gray-400">{t("noVersions")}</p>
      ) : (
        <div className="space-y-1">
          {versions.map((v) => (
            <div
              key={v.version}
              role="button"
              tabIndex={0}
              onClick={() => handleSelect(v.version)}
              onKeyDown={(e) => {
                if (e.key === "Enter" || e.key === " ") handleSelect(v.version);
              }}
              className={cn(
                "w-full cursor-pointer rounded-lg px-2.5 py-2 text-left transition-colors",
                previewVersion === v.version ? "bg-white/[0.08]" : "hover:bg-white/[0.04]"
              )}
            >
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-gray-200">v{v.version}</span>
                {v.version === currentVersion && (
                  <span className="text-xs text-emerald-400">{t("currentVersion")}</span>
                )}
              </div>
              <p className="mt-0.5 text-xs text-gray-400">
                {v.changedByAgentName ??
                  v.changedByUserName ??
                  (v.changedByAgentId ? t("agentAuthor") : t("userAuthor"))}{" "}
                · {formatRelativeTime(v.createdAt)}
              </p>
              {v.changedByAgentId && v.requestedByUserName && (
                <p className="mt-0.5 text-xs text-gray-400">
                  {t("askedBy", { name: v.requestedByUserName })}
                </p>
              )}
              {v.changedByAgentId && v.sourceConversationId && (
                <Link
                  href={`/p/${projectId}/team?agent=${v.changedByAgentId}&tab=chat&c=${v.sourceConversationId}${v.sourceMessageId ? `&m=${v.sourceMessageId}` : ""}`}
                  onClick={(e) => e.stopPropagation()}
                  className="mt-1 inline-flex items-center gap-1 text-xs text-primary-400 transition-colors hover:text-primary-300"
                >
                  <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                    <path d="M21 15a2 2 0 01-2 2H7l-4 4V5a2 2 0 012-2h14a2 2 0 012 2z" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                  {t("viewInChat")}
                </Link>
              )}
              {previewVersion === v.version && v.version !== currentVersion && (
                <RequirePermission permission="AgentsChat">
                  <Button
                    size="sm"
                    className="mt-1.5 w-full"
                    disabled={isRestoring}
                    onClick={(e) => {
                      e.stopPropagation();
                      handleRestore(v.version);
                    }}
                  >
                    {isRestoring ? t("restoring") : t("restoreVersion")}
                  </Button>
                </RequirePermission>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

export { DiagramHistoryPanel };
